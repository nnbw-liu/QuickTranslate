using System.Windows;
using QuickTranslate.Models;
using QuickTranslate.Views;

namespace QuickTranslate.Services;

/// <summary>翻译/回复/截图翻译的执行编排：捕获 → 流式调用模型 → 弹窗逐字展示。</summary>
public sealed class TranslateController
{
    private PopupWindow? _active;
    private InputTranslateWindow? _input;
    private CancellationTokenSource? _current;

    /// <summary>取消当前正在进行的请求（用户再按一次热键时用于打断上一次）。</summary>
    public void CancelCurrent()
    {
        try { _current?.Cancel(); } catch { }
        _input?.CancelCurrent(); // 输入翻译窗口里跑着自己的流
    }

    /// <summary>当前是否有「输入翻译」窗口开着。</summary>
    public bool HasInputWindow => _input is { IsLoaded: true };

    public async Task ExecuteAsync(HotkeyEntry entry)
    {
        // 输入翻译：不捕获选中内容，直接弹输入框让用户自己填
        if (entry.Action == "InputTranslate")
        {
            ShowInputTranslate(entry);
            return;
        }

        CloseActive();      // 关闭旧弹窗（其 Closed 事件会取消旧请求的流）
        CloseInputTranslate(); // 输入翻译窗口会抢焦点，先关掉再捕获，否则读不到用户选中的内容
        // 等焦点真正回到原来的程序再捕获：否则 UIA 读到的是本程序弹窗、Ctrl+C 也复制不到
        // 用户选中的内容（表现为“按了热键却没翻译选中的文本”，而且是时好时坏）。
        await WaitForForeignForegroundAsync();
        Native.NativeWin.GetCursor(out int cx, out int cy);

        // 先解析「这次用哪个 API 的哪个模型」，再动手捕获/框选：
        // 没有可用模型时直接提示，而不是让用户白框一次屏幕。
        ModelSelection sel;
        try
        {
            sel = App.Models.Resolve(entry);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            var warn = NewPopup();
            warn.ShowLoading(entry.Name, cx, cy);
            warn.ShowError(ex.Message);
            return;
        }
        Log.Info($"执行: {entry.Name} [{entry.Action}] 目标={entry.TargetLanguage} 模型={sel.Describe()}");

        using var cts = new CancellationTokenSource();
        _current = cts;
        PopupWindow? popup = null;
        try
        {
            if (entry.Action == "ScreenshotOcr")
            {
                // 先框选截图、后弹浮窗：顺序反了会把浮窗自己截进图里（也会抢走焦点）。
                var region = ScreenCaptureWindow.PickRegion();
                if (region == null)
                {
                    Log.Info("截图翻译: 用户取消了框选");
                    return;
                }
                await Task.Delay(160); // 等遮罩窗口消失、屏幕重绘完成
                var shot = await Task.Run(() => SelectionCapture.CaptureRegion(region.Value));
                Log.Info($"截图: 区域={region.Value.Width}x{region.Value.Height}@{region.Value.X},{region.Value.Y} 字节={shot.ImageBytes?.Length ?? 0}");

                popup = NewPopup();
                popup.Closed += (_, _) => { try { cts.Cancel(); } catch { } };
                popup.ShowLoading(entry.Name, cx, cy);
                popup.SetModel(sel.Display);

                if (!shot.HasContent)
                {
                    popup.ShowError("屏幕截图失败。");
                    return;
                }
                await RunScreenshotOcrAsync(popup, entry, shot, sel, cts.Token);
                return;
            }

            // 关键：先捕获、后弹窗。弹窗若在捕获前激活会抢走焦点，
            // 导致第二次起 UIA 读到弹窗内容、Ctrl+C 也复制不到用户选中的内容。
            var cap = await Task.Run(() => SelectionCapture.Capture());
            if (!cap.HasContent)
            {
                await Task.Delay(150);
                cap = await Task.Run(() => SelectionCapture.Capture());
            }
            Log.Info($"捕获: kind={cap.Kind} textLen={cap.Text.Length} imgLen={cap.ImageBytes?.Length ?? 0}");

            popup = NewPopup();
            popup.Closed += (_, _) => { try { cts.Cancel(); } catch { } };
            popup.ShowLoading(entry.Name, cx, cy);
            popup.SetModel(sel.Display);

            if (!cap.HasContent)
            {
                popup.ShowNoSelection(cx, cy, entry);
                return;
            }

            popup.SetOriginal(cap);
            popup.BeginStream(entry.Name);

            // 按动作分流：生成回复 与 翻译 走不同的提示词与调用方式
            if (entry.Action == "GenerateReply")
                await RunReplyAsync(popup, cap, ReplyLang(entry), ReplyPrompt(entry), sel, cts.Token);
            else
                await TranslateCaptureStreamAsync(popup, cap, entry.TargetLanguage, TranslatePrompt(entry), sel, cts.Token);

            Log.Info($"完成: 结果长度={popup.ResultLength}");
            popup.EndStream(entry);
        }
        catch (OperationCanceledException)
        {
            Log.Info("操作已取消（弹窗关闭或新请求打断）");
            if (popup != null && popup.IsVisible) { try { popup.Close(); } catch { } }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            if (popup != null && popup.IsVisible) popup.ShowError("出错了：\n" + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_current, cts)) _current = null;
        }
    }

    private PopupWindow NewPopup()
    {
        var popup = new PopupWindow();
        _active = popup;
        popup.RequestAction += async (p, action) => await HandlePopupActionAsync(p, action);
        return popup;
    }

    // ---------- 输入翻译（热键 → 输入框 → 翻译） ----------

    /// <summary>
    /// 打开（已打开则复用并聚焦）「输入翻译」窗口：始终置顶，可切换目标语言，
    /// 输入内容后 Ctrl+Enter/「翻译」按钮开始流式翻译，Esc 关闭并中断推理。
    /// </summary>
    public void ShowInputTranslate(HotkeyEntry entry)
    {
        CloseActive(); // 关掉旧的翻译结果浮窗

        if (_input is { IsLoaded: true })
        {
            _input.ApplyEntry(entry);
            if (_input.WindowState == WindowState.Minimized) _input.WindowState = WindowState.Normal;
            _input.Activate();
            _input.FocusInput();
            Log.Info($"输入翻译: 复用已打开的窗口 [{entry.Name}]");
            return;
        }

        var w = new InputTranslateWindow(entry);
        _input = w;
        w.Closed += (_, _) => { if (ReferenceEquals(_input, w)) _input = null; };
        w.Show();
        w.Activate();
        w.Topmost = true; // 保证置顶（有些窗口在激活时会被系统改掉）
        w.FocusInput();
        Log.Info($"输入翻译: 窗口已打开 [{entry.Name}] 目标语言={entry.TargetLanguage}");
    }

    /// <summary>关闭「输入翻译」窗口（执行捕获类动作前必须先关，否则它会抢走焦点）。</summary>
    public void CloseInputTranslate()
    {
        var w = _input;
        _input = null;
        try { w?.Close(); } catch { }
    }

    /// <summary>把用户输入的文字流式翻译进窗口（译文 + 思考过程都实时显示）。</summary>
    public static async Task TranslateTextIntoAsync(IStreamSink sink, string text, string lang, string? prompt, HotkeyEntry? entry, CancellationToken ct)
    {
        var sel = App.Models.Resolve(entry);
        sink.SetModel(sel.Display);
        Log.Info($"输入翻译: 开始 目标语言={lang} 输入长度={text.Length} 模型={sel.Describe()}");
        await StreamIntoAsync(sink, App.Models.ClientFor(sel).TranslateStreamAsync(text, lang, ct, prompt), sel, ct);
    }

    /// <summary>
    /// 截图翻译：按当前这次请求所用 API 的「图片翻译方式」决定路径（首选项 → 模型）。
    /// Vision：截图直接发视觉模型（不做本地 OCR，快且保排版）；失败才回退本地 OCR。
    /// Ocr：先本地 OCR 识别文字再翻译；OCR 无结果时才把原图交给模型。
    /// </summary>
    private async Task RunScreenshotOcrAsync(PopupWindow popup, HotkeyEntry entry, CaptureResult cap, ModelSelection sel, CancellationToken ct)
    {
        var target = string.IsNullOrWhiteSpace(entry.TargetLanguage) ? "中文" : entry.TargetLanguage;
        var prompt = ScreenshotPrompt(entry);
        var client = App.Models.ClientFor(sel);
        var visionFirst = sel.VisionFirst;

        popup.SetOriginal(cap); // 截图没有可展示的“原文”文本，图片模式下不展开原文区
        popup.BeginStream(entry.Name);
        popup.SetModel(sel.Display);

        if (visionFirst)
        {
            popup.SetStatus("正在把截图发给视觉模型…");
            Log.Info($"截图翻译: 视觉模型直发（未做本地 OCR） 模型={sel.Display}");
            try
            {
                await StreamIntoAsync(popup, client.TranslateImageStreamAsync(cap.ImageBytes!, target, ct, prompt), sel, ct);
                popup.EndStream(entry);
                return;
            }
            catch (ApiException ex)
            {
                Log.Info($"截图翻译: 视觉模型失败，回退本地 OCR：{ex.Message}");
                popup.ClearResult();
                popup.SetStatus("视觉模型不可用，改用本地 OCR…");
            }
        }

        var ocr = await SafeOcrAsync(cap.ImageBytes!);
        if (!string.IsNullOrWhiteSpace(ocr))
        {
            popup.SetOriginal(new CaptureResult { Kind = CaptureKind.Text, Text = ocr });
            popup.SetStatus("正在翻译识别结果…");
            Log.Info("截图翻译: 开始翻译识别结果");
            await StreamIntoAsync(popup, client.TranslateStreamAsync(ocr, target, ct, prompt), sel, ct);
            popup.EndStream(entry);
            return;
        }

        if (visionFirst)
            throw new ApiException("截图翻译失败：视觉模型与本地 OCR 均未处理成功。");

        popup.SetStatus("本地 OCR 未识别到文字，改发原图给模型…");
        await StreamIntoAsync(popup, client.TranslateImageStreamAsync(cap.ImageBytes!, target, ct, prompt), sel, ct);
        popup.EndStream(entry);
    }

    /// <summary>回复语言：热键自己的目标语言；没设（如托盘「生成回复」）则跟随原文语言。</summary>
    private static string? ReplyLang(HotkeyEntry? e)
        => string.IsNullOrWhiteSpace(e?.TargetLanguage) ? null : e.TargetLanguage;

    /// <summary>生成回复：文本直接用，图片先 OCR 再回复（OCR 失败则把原图交给模型）。</summary>
    private static async Task RunReplyAsync(PopupWindow p, CaptureResult cap, string? lang, string? prompt, ModelSelection sel, CancellationToken ct)
    {
        var client = App.Models.ClientFor(sel);
        p.SetModel(sel.Display);

        if (cap.Kind == CaptureKind.Text)
        {
            p.SetStatus("正在生成回复…");
            await StreamIntoAsync(p, client.GenerateReplyStreamAsync(cap.Text, lang, ct, prompt), sel, ct);
            return;
        }

        p.SetStatus("正在识别图片文字(OCR)…");
        var ocr = await SafeOcrAsync(cap.ImageBytes!);
        if (string.IsNullOrWhiteSpace(ocr))
        {
            p.SetStatus("本地 OCR 未识别到文字，改发原图给模型…");
            await StreamIntoAsync(p, client.GenerateReplyFromImageStreamAsync(cap.ImageBytes!, lang, ct, prompt), sel, ct);
            return;
        }
        p.SetOriginal(new CaptureResult { Kind = CaptureKind.Text, Text = ocr });
        p.SetStatus("正在生成回复…");
        await StreamIntoAsync(p, client.GenerateReplyStreamAsync(ocr, lang, ct, prompt), sel, ct);
    }

    // ---------- 提示词解析：热键单独设置 > 功能默认 > 内置默认 ----------

    private static string? ResolvePrompt(string? entryPrompt, string? actionDefault)
        => !string.IsNullOrWhiteSpace(entryPrompt) ? entryPrompt.Trim()
         : !string.IsNullOrWhiteSpace(actionDefault) ? actionDefault.Trim()
         : null;

    /// <summary>翻译提示词（热键单独设置 > 翻译功能默认 > 内置默认）；「输入翻译」窗口也用它。</summary>
    public static string? TranslatePrompt(HotkeyEntry? e) => ResolvePrompt(e?.Prompt, App.Settings.General.TranslatePrompt);
    private static string? ScreenshotPrompt(HotkeyEntry? e) => ResolvePrompt(e?.Prompt, App.Settings.General.ScreenshotPrompt);
    private static string? ReplyPrompt(HotkeyEntry? e) => ResolvePrompt(e?.Prompt, App.Settings.General.ReplyPrompt);

    /// <summary>把一次流式调用逐字写入弹窗；流式结束后校验非空。</summary>
    private static async Task TranslateCaptureStreamAsync(PopupWindow popup, CaptureResult cap, string lang, string? prompt, ModelSelection sel, CancellationToken ct)
    {
        var client = App.Models.ClientFor(sel);
        popup.SetModel(sel.Display);
        Log.Info($"开始翻译: 目标语言={lang} 模型={sel.Display}");
        if (cap.Kind == CaptureKind.Image)
        {
            bool visionTried = false;
            if (sel.VisionFirst)
            {
                try
                {
                    popup.SetStatus("正在发送图片到模型…");
                    await StreamIntoAsync(popup, client.TranslateImageStreamAsync(cap.ImageBytes!, lang, ct, prompt), sel, ct);
                    return;
                }
                catch (ApiException ex)
                {
                    Log.Info($"图片翻译: 视觉模型失败，回退本地 OCR：{ex.Message}");
                    visionTried = true; // 视觉失败 → 回退本地 OCR
                    popup.ClearResult();
                }
            }

            // OCR 路径：即使 OCR 失败/中断，也回退视觉模型保证能继续翻译
            popup.SetStatus("正在识别图片文字(OCR)…");
            var ocr = await SafeOcrAsync(cap.ImageBytes!);
            if (!string.IsNullOrWhiteSpace(ocr))
            {
                popup.SetOriginal(new CaptureResult { Kind = CaptureKind.Text, Text = ocr });
                popup.SetStatus("正在翻译识别结果…");
                await StreamIntoAsync(popup, client.TranslateStreamAsync(ocr, lang, ct, prompt), sel, ct);
                return;
            }

            if (visionTried)
                throw new ApiException("图片翻译失败：视觉模型与本地 OCR 均未处理成功。");
            popup.SetStatus("本地 OCR 未识别到文字，改发原图给模型…");
            await StreamIntoAsync(popup, client.TranslateImageStreamAsync(cap.ImageBytes!, lang, ct, prompt), sel, ct);
            return;
        }

        await StreamIntoAsync(popup, client.TranslateStreamAsync(cap.Text, lang, ct, prompt), sel, ct);
    }

    /// <summary>
    /// 把流式增量逐字写入界面（浮窗或「输入翻译」窗口，两者都实现 <see cref="IStreamSink"/>）。
    /// 思考内容(reasoning)一律**边到边实时显示**，不再攒到最后一次性贴出来：
    /// 即使模型先思考很久（关不掉思考的接口很常见），用户也能看到进度，而不是只盯着转圈。
    /// 结束时按 finish_reason 判断是否被「最大输出 Token」截断，并在界面上给出可操作提示。
    /// </summary>
    private static async Task StreamIntoAsync(IStreamSink sink, IAsyncEnumerable<StreamDelta> stream, ModelSelection sel, CancellationToken ct)
    {
        sink.SetModel(sel.Display);
        Log.Info($"流式开始（{sel.Display}）");
        int blocks = 0, contentBlocks = 0, reasoningChars = 0;
        string? finish = null;
        int? completionTokens = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long firstReasoningMs = -1, firstContentMs = -1;

        await foreach (var d in stream.WithCancellation(ct))
        {
            if (d.FinishReason is not null) finish = d.FinishReason;
            if (d.CompletionTokens is int used) completionTokens = used;

            if (!string.IsNullOrEmpty(d.Reasoning))
            {
                if (firstReasoningMs < 0) firstReasoningMs = sw.ElapsedMilliseconds;
                reasoningChars += d.Reasoning.Length;
                sink.AppendReasoningChunk(d.Reasoning);                     // 实时增量显示（含首块）
                if (firstContentMs < 0)
                    sink.SetStatus($"模型正在思考…（已 {reasoningChars} 字）");
            }

            if (!string.IsNullOrEmpty(d.Content))
            {
                if (firstContentMs < 0)
                {
                    firstContentMs = sw.ElapsedMilliseconds;
                    sink.SetStatus("正在输出…");
                }
                contentBlocks++;
                sink.AppendResultChunk(d.Content);
            }
            blocks++;
        }

        Log.Info($"流式接收: 块数={blocks} 正文块={contentBlocks} 首字耗时={firstContentMs}ms " +
                 $"首思考耗时={firstReasoningMs}ms 思考字数={reasoningChars} finish={finish ?? "?"} " +
                 $"用量={completionTokens?.ToString() ?? "?"} 内容长度={sink.ResultLength}");

        // 最大输出 Token 是否真的生效：把「被截断」这件事明确告诉用户（额度按这个 API 自己的设置算）
        if (finish == "length")
            sink.SetNotice($"已达到「最大输出 Token」上限（{sel.Display} 当前 {sel.MaxTokens}）：" +
                            "输出被接口截断（思考过程也算在这个额度里）。可在首选项 → 模型 里调大该值。");
        else if (finish == "client_limit")
            sink.SetNotice($"已达到「最大输出 Token」上限（{sel.Display} 当前 {sel.MaxTokens}）：" +
                            "程序已主动停止接收剩余输出。可在首选项 → 模型 里调大该值。");

        if (sink.ResultLength == 0)
        {
            if (reasoningChars > 0)
            {
                sink.SetStatus("模型只输出了思考过程、没有正文（思考内容见上方）");
                if (!sink.HasNotice)
                    sink.SetNotice($"这次的最大输出 Token（{sel.Display} 当前 {sel.MaxTokens}）可能全被思考过程用掉了：" +
                                    "可调大该值，或改用非思考型模型。");
                return;
            }
            throw new ApiException($"{sel.Display} 没有返回任何内容，请检查模型名称与请求参数（可在首选项 → 模型 里测试连接）。");
        }
    }

    /// <summary>OCR 的健壮封装：任何异常/中断都不会向外抛，只返回 null（触发回退）。</summary>
    private static async Task<string?> SafeOcrAsync(byte[] png)
    {
        try
        {
            var r = await OcrService.RecognizeAsync(png);
            Log.Info($"OCR 结果: 长度={r?.Length ?? 0} 预览={(string.IsNullOrWhiteSpace(r) ? "(空)" : Shorten(r!, 60))}");
            return r;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
    }

    private static string Shorten(string s, int max)
    {
        var t = string.Join(" ", s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }

    private async Task HandlePopupActionAsync(PopupWindow p, string action)
    {
        if (p.IsStreaming)
        {
            p.SetStatus("正在生成中，请稍候…");
            return;
        }

        var cap = p.Capture;
        if (cap == null) return;

        // 浮窗里的手动动作沿用「触发它的那条热键」的语言，但提示词用功能默认：
        // 热键单独设置的提示词只服务于该热键自己的动作（例如一条专门回邮件的热键）。
        var keep = p.Entry;

        using var cts = new CancellationTokenSource();
        p.Closed += (_, _) => { try { cts.Cancel(); } catch { } };

        try
        {
            switch (action)
            {
                case "close":
                    p.Close();
                    break;

                case "ocr":
                    p.Close();
                    await ExecuteAsync(new HotkeyEntry { Name = "截图翻译", Action = "ScreenshotOcr", TargetLanguage = "中文" });
                    break;

                case "retranslate":
                {
                    var lang = string.IsNullOrWhiteSpace(keep?.TargetLanguage) ? "中文" : keep!.TargetLanguage;
                    p.BeginStream(keep?.Name ?? "翻译");
                    p.SetStatus("正在重新翻译…");
                    // 浮窗里的手动动作按「功能」取模型（不受触发它的那条热键的模型覆盖影响）
                    await TranslateCaptureStreamAsync(p, cap, lang, TranslatePrompt(null), App.Models.Resolve(), cts.Token);
                    p.EndStream(keep ?? new HotkeyEntry { Name = "翻译", TargetLanguage = lang });
                    break;
                }

                case "reply":
                {
                    p.BeginStream("生成回复");
                    p.SetStatus("正在生成回复…");
                    await RunReplyAsync(p, cap, ReplyLang(keep), ReplyPrompt(null), App.Models.Resolve(), cts.Token);
                    p.EndStream(new HotkeyEntry { Name = "生成回复", Action = "GenerateReply", TargetLanguage = keep?.TargetLanguage ?? "" });
                    break;
                }

                default:
                    if (action.StartsWith("translate:"))
                    {
                        var lang = action["translate:".Length..];
                        p.BeginStream($"翻译为{lang}");
                        p.SetStatus($"正在翻译为 {lang}…");
                        await TranslateCaptureStreamAsync(p, cap, lang, TranslatePrompt(null), App.Models.Resolve(), cts.Token);
                        p.EndStream(new HotkeyEntry { Name = $"翻译为{lang}", TargetLanguage = lang });
                    }
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("弹窗动作已取消");
        }
        catch (Exception ex)
        {
            p.ShowError("出错了：\n" + ex.Message);
            Log.Error(ex);
        }
    }

    public void CloseActive()
    {
        var p = _active;
        _active = null;
        try { p?.Close(); } catch { }
    }

    /// <summary>
    /// 等待前台窗口不再是本程序（弹窗关闭后焦点要还给用户原来的程序）。
    /// 这是“有时按热键没翻译选中的文本”的根因：旧弹窗还占着焦点时，
    /// UIA 只能看到弹窗自己，模拟 Ctrl+C 也复制不到东西。
    /// </summary>
    private static async Task WaitForForeignForegroundAsync(int timeoutMs = 600)
    {
        for (int waited = 0; waited <= timeoutMs; waited += 35)
        {
            var fg = Native.NativeWin.GetForegroundWindow();
            if (fg == IntPtr.Zero) return;
            Native.NativeWin.GetWindowThreadProcessId(fg, out uint pid);
            if (pid != Environment.ProcessId) return;
            await Task.Delay(35);
        }
        Log.Info("等待焦点回到其它程序超时（当前前台仍是本程序）");
    }
}
