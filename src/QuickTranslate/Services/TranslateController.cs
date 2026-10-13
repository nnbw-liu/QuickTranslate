using System.Text;
using QuickTranslate.Models;
using QuickTranslate.Views;

namespace QuickTranslate.Services;

/// <summary>翻译/回复/截图翻译的执行编排：捕获 → 流式调用模型 → 弹窗逐字展示。</summary>
public sealed class TranslateController
{
    private PopupWindow? _active;
    private CancellationTokenSource? _current;

    /// <summary>取消当前正在进行的请求（用户再按一次热键时用于打断上一次）。</summary>
    public void CancelCurrent()
    {
        try { _current?.Cancel(); } catch { }
    }

    public async Task ExecuteAsync(HotkeyEntry entry)
    {
        CloseActive(); // 关闭旧弹窗（其 Closed 事件会取消旧请求的流）
        // 等焦点真正回到原来的程序再捕获：否则 UIA 读到的是本程序弹窗、Ctrl+C 也复制不到
        // 用户选中的内容（表现为“按了热键却没翻译选中的文本”，而且是时好时坏）。
        await WaitForForeignForegroundAsync();
        Native.NativeWin.GetCursor(out int cx, out int cy);
        Log.Info($"执行: {entry.Name} [{entry.Action}] 目标={entry.TargetLanguage}");

        using var cts = new CancellationTokenSource();
        _current = cts;
        PopupWindow? popup = null;
        try
        {
            if (entry.Action == "ScreenshotOcr")
            {
                // 先框选截图、后弹浮窗：顺序反了会把浮窗自己截进图里（也会抢走焦点）。
                var sel = ScreenCaptureWindow.PickRegion();
                if (sel == null)
                {
                    Log.Info("截图翻译: 用户取消了框选");
                    return;
                }
                await Task.Delay(160); // 等遮罩窗口消失、屏幕重绘完成
                var shot = await Task.Run(() => SelectionCapture.CaptureRegion(sel.Value));
                Log.Info($"截图: 区域={sel.Value.Width}x{sel.Value.Height}@{sel.Value.X},{sel.Value.Y} 字节={shot.ImageBytes?.Length ?? 0}");

                popup = NewPopup();
                popup.Closed += (_, _) => { try { cts.Cancel(); } catch { } };
                popup.ShowLoading(entry.Name, cx, cy);

                if (!shot.HasContent)
                {
                    popup.ShowError("屏幕截图失败。");
                    return;
                }
                await RunScreenshotOcrAsync(popup, entry, shot, cts.Token);
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

            if (!cap.HasContent)
            {
                popup.ShowNoSelection(cx, cy, entry);
                return;
            }

            popup.SetOriginal(cap);
            popup.BeginStream(entry.Name);

            // 按动作分流：生成回复 与 翻译 走不同的提示词与调用方式
            if (entry.Action == "GenerateReply")
                await RunReplyAsync(popup, cap, ReplyLang(entry), ReplyPrompt(entry), cts.Token);
            else
                await TranslateCaptureStreamAsync(popup, cap, entry.TargetLanguage, TranslatePrompt(entry), cts.Token);

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

    /// <summary>
    /// 截图翻译：按「设置 → API 设置 → 图片翻译方式」决定路径。
    /// Vision：截图直接发视觉模型（不做本地 OCR，快且保排版）；失败才回退本地 OCR。
    /// Ocr：先本地 OCR 识别文字再翻译；OCR 无结果时才把原图交给模型。
    /// </summary>
    private async Task RunScreenshotOcrAsync(PopupWindow popup, HotkeyEntry entry, CaptureResult cap, CancellationToken ct)
    {
        var target = string.IsNullOrWhiteSpace(entry.TargetLanguage) ? "中文" : entry.TargetLanguage;
        var prompt = ScreenshotPrompt(entry);
        var visionFirst = !string.Equals(App.Settings.Api.ImageMode, "Ocr", StringComparison.OrdinalIgnoreCase);

        popup.SetOriginal(cap); // 截图没有可展示的“原文”文本，图片模式下不展开原文区
        popup.BeginStream(entry.Name);

        if (visionFirst)
        {
            popup.SetStatus("正在把截图发给视觉模型…");
            Log.Info($"截图翻译: 视觉模型直发（未做本地 OCR）");
            try
            {
                await StreamIntoAsync(popup, App.Api.TranslateImageStreamAsync(cap.ImageBytes!, target, ct, prompt), ct, TranslateShowsReasoning);
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
            await StreamIntoAsync(popup, App.Api.TranslateStreamAsync(ocr, target, ct, prompt), ct, TranslateShowsReasoning);
            popup.EndStream(entry);
            return;
        }

        if (visionFirst)
            throw new ApiException("截图翻译失败：视觉模型与本地 OCR 均未处理成功。");

        popup.SetStatus("本地 OCR 未识别到文字，改发原图给模型…");
        await StreamIntoAsync(popup, App.Api.TranslateImageStreamAsync(cap.ImageBytes!, target, ct, prompt), ct, TranslateShowsReasoning);
        popup.EndStream(entry);
    }

    /// <summary>回复语言：热键自己的目标语言；没设（如托盘「生成回复」）则跟随原文语言。</summary>
    private static string? ReplyLang(HotkeyEntry? e)
        => string.IsNullOrWhiteSpace(e?.TargetLanguage) ? null : e.TargetLanguage;

    /// <summary>生成回复：文本直接用，图片先 OCR 再回复（OCR 失败则把原图交给模型）。</summary>
    private static async Task RunReplyAsync(PopupWindow p, CaptureResult cap, string? lang, string? prompt, CancellationToken ct)
    {
        if (cap.Kind == CaptureKind.Text)
        {
            p.SetStatus("正在生成回复…");
            await StreamIntoAsync(p, App.Api.GenerateReplyStreamAsync(cap.Text, lang, ct, prompt), ct);
            return;
        }

        p.SetStatus("正在识别图片文字(OCR)…");
        var ocr = await SafeOcrAsync(cap.ImageBytes!);
        if (string.IsNullOrWhiteSpace(ocr))
        {
            p.SetStatus("本地 OCR 未识别到文字，改发原图给模型…");
            await StreamIntoAsync(p, App.Api.GenerateReplyFromImageStreamAsync(cap.ImageBytes!, lang, ct, prompt), ct);
            return;
        }
        p.SetOriginal(new CaptureResult { Kind = CaptureKind.Text, Text = ocr });
        p.SetStatus("正在生成回复…");
        await StreamIntoAsync(p, App.Api.GenerateReplyStreamAsync(ocr, lang, ct, prompt), ct);
    }

    // ---------- 提示词解析：热键单独设置 > 功能默认 > 内置默认 ----------

    private static string? ResolvePrompt(string? entryPrompt, string? actionDefault)
        => !string.IsNullOrWhiteSpace(entryPrompt) ? entryPrompt.Trim()
         : !string.IsNullOrWhiteSpace(actionDefault) ? actionDefault.Trim()
         : null;

    private static string? TranslatePrompt(HotkeyEntry? e) => ResolvePrompt(e?.Prompt, App.Settings.General.TranslatePrompt);
    private static string? ScreenshotPrompt(HotkeyEntry? e) => ResolvePrompt(e?.Prompt, App.Settings.General.ScreenshotPrompt);
    private static string? ReplyPrompt(HotkeyEntry? e) => ResolvePrompt(e?.Prompt, App.Settings.General.ReplyPrompt);

    /// <summary>翻译类请求是否展示思考过程（由「通用」里的“跳过思考过程”开关决定）。</summary>
    private static bool TranslateShowsReasoning => !App.Settings.General.SkipReasoningForTranslation;

    /// <summary>把一次流式调用逐字写入弹窗；流式结束后校验非空。</summary>
    private static async Task TranslateCaptureStreamAsync(PopupWindow popup, CaptureResult cap, string lang, string? prompt, CancellationToken ct)
    {
        Log.Info($"开始翻译: 目标语言={lang} 模型={App.Api.Model}");
        if (cap.Kind == CaptureKind.Image)
        {
            bool visionTried = false;
            if (App.Settings.Api.ImageMode == "Vision")
            {
                try
                {
                    popup.SetStatus("正在发送图片到模型…");
                    await StreamIntoAsync(popup, App.Api.TranslateImageStreamAsync(cap.ImageBytes!, lang, ct, prompt), ct, TranslateShowsReasoning);
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
                await StreamIntoAsync(popup, App.Api.TranslateStreamAsync(ocr, lang, ct, prompt), ct, TranslateShowsReasoning);
                return;
            }

            if (visionTried)
                throw new ApiException("图片翻译失败：视觉模型与本地 OCR 均未处理成功。");
            popup.SetStatus("本地 OCR 未识别到文字，改发原图给模型…");
            await StreamIntoAsync(popup, App.Api.TranslateImageStreamAsync(cap.ImageBytes!, lang, ct, prompt), ct, TranslateShowsReasoning);
            return;
        }

        await StreamIntoAsync(popup, App.Api.TranslateStreamAsync(cap.Text, lang, ct, prompt), ct, TranslateShowsReasoning);
    }

    /// <summary>
    /// 把流式增量写入弹窗；记录块数用于诊断。
    /// 翻译默认不展示思考过程，但会暂存：若最终没有正文（思考型模型常见），
    /// 就把思考内容显示出来并给出可操作提示，而不是报一句“没有生成内容”。
    /// </summary>
    private static async Task StreamIntoAsync(PopupWindow popup, IAsyncEnumerable<StreamDelta> stream, CancellationToken ct, bool showReasoning = true)
    {
        Log.Info("流式开始");
        int n = 0, contentChunks = 0;
        var hidden = new StringBuilder();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long firstContentMs = -1;
        await foreach (var d in stream.WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(d.Reasoning))
            {
                if (showReasoning) popup.AppendReasoningChunk(d.Reasoning);
                else hidden.Append(d.Reasoning);
            }
            if (!string.IsNullOrEmpty(d.Content))
            {
                if (firstContentMs < 0) firstContentMs = sw.ElapsedMilliseconds;
                contentChunks++;
                popup.AppendResultChunk(d.Content);
            }
            n++;
        }
        Log.Info($"流式接收: 块数={n} 正文块={contentChunks} 首字耗时={firstContentMs}ms 内容长度={popup.ResultLength} 隐藏思考长度={hidden.Length}");
        if (popup.ResultLength == 0)
        {
            if (hidden.Length > 0)
            {
                popup.AppendReasoningChunk(hidden.ToString());
                popup.SetStatus("模型只输出了思考过程，没有正文（思考内容见下方）。可加大「最大输出 Token」" +
                                $"（当前 {App.Settings.Api.MaxTokens}），或在「通用」里关闭“翻译时跳过思考过程”。");
                return;
            }
            throw new ApiException("API 响应中没有生成内容，请检查模型名称与请求参数。");
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
                    await TranslateCaptureStreamAsync(p, cap, lang, TranslatePrompt(null), cts.Token);
                    p.EndStream(keep ?? new HotkeyEntry { Name = "翻译", TargetLanguage = lang });
                    break;
                }

                case "reply":
                {
                    p.BeginStream("生成回复");
                    p.SetStatus("正在生成回复…");
                    await RunReplyAsync(p, cap, ReplyLang(keep), ReplyPrompt(null), cts.Token);
                    p.EndStream(new HotkeyEntry { Name = "生成回复", Action = "GenerateReply", TargetLanguage = keep?.TargetLanguage ?? "" });
                    break;
                }

                default:
                    if (action.StartsWith("translate:"))
                    {
                        var lang = action["translate:".Length..];
                        p.BeginStream($"翻译为{lang}");
                        p.SetStatus($"正在翻译为 {lang}…");
                        await TranslateCaptureStreamAsync(p, cap, lang, TranslatePrompt(null), cts.Token);
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
