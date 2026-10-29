using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using SDemo.QuickTranslate.Models;
using SDemo.QuickTranslate.Services;
using SDemo.QuickTranslate.Views;

namespace SDemo.QuickTranslate;

/// <summary>应用入口：无主窗口，启动即托盘后台运行。</summary>
public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static SettingsService SettingsService { get; private set; } = null!;
    /// <summary>模型编排：把「热键 → 功能 → 默认模型」解析成具体的 API + 模型，并按需缓存客户端。</summary>
    public static ApiCatalog Models { get; private set; } = null!;
    public static TranslateController Controller { get; private set; } = null!;
    /// <summary>全局热键管理器（首选项窗口打开期间会被暂停）。</summary>
    public static HotkeyManager? Hotkeys { get; private set; }

    private Mutex? _mutex;
    private TrayIconService? _tray;
    private PreferencesWindow? _prefsWindow;
    private bool _busy;
    private Task? _running;
    private bool _hotkeysSuspended;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) => { Log.Error(args.Exception); args.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error(args.ExceptionObject as Exception ?? new Exception("unknown unhandled exception"));

        SettingsService = new SettingsService();
        Settings = SettingsService.Load();

        // 模型编排与控制器：即使在界面调试模式下也要建好，
        // 这样 --window=input 的输入翻译窗口可以真的发起翻译（方便调试/端到端验证）。
        Models = new ApiCatalog();
        Controller = new TranslateController();

        // 界面调试/截图模式（--window=prefs|models|help|popup|input）：只打开指定窗口，
        // 不占用单实例互斥量、不注册托盘图标与热键；关闭窗口即退出。
        var windowArg = e.Args.FirstOrDefault(a => a.StartsWith("--window=", StringComparison.OrdinalIgnoreCase));
        if (windowArg != null)
        {
            var which = windowArg["--window=".Length..].ToLowerInvariant();
            Window w = which switch
            {
                "help" => new AboutWindow(),
                "popup" => BuildDemoPopup(),
                "input" => BuildDemoInputWindow(),
                // models = 首选项的「模型」页（多 API 设置），方便截图与调试
                "models" => new PreferencesWindow("models"),
                _ => new PreferencesWindow(),
            };
            w.Closed += (_, _) => Shutdown();
            w.Show();
            w.Activate();
            return;
        }

        // 自检模式（--selftest）：不开界面、不占托盘，直接把一段文字走一遍流式翻译/生成回复，
        // 把「关闭思考参数是否生效、思考/正文是否真的流式、最大输出 Token 是否被截断」写进日志。
        // 例：SDemo.QuickTranslate.exe --selftest --text="Hello" --to=中文 --tokens=64 [--think] [--reply]
        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            _ = RunSelfTestAsync(e.Args);
            return;
        }

        // 单实例
        _mutex = new Mutex(true, @"Local\SDemo.QuickTranslate.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("SDemo.QuickTranslate 已经在运行了。", "SDemo.QuickTranslate",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ApplyAutoStart(Settings.General.AutoStart, quiet: true);

        Hotkeys = new HotkeyManager();
        Hotkeys.Init();
        Hotkeys.ActionTriggered += OnHotkey;

        _tray = new TrayIconService();
        _tray.MenuAction += OnTrayAction;
        _tray.Init();
        _tray.RebuildMenu(Settings);

        var errs = Hotkeys.RegisterAll(Settings);
        if (errs.Count > 0)
            _tray.ShowBalloon("热键注册提示", string.Join("\n", errs), warning: true);

        // 远程 API 还没填 Key 时提醒一次（本机接口通常不需要 Key，不打扰）
        var missingKey = Settings.Providers
            .Where(p => string.IsNullOrWhiteSpace(p.ApiKey) && !p.IsLocalAddress)
            .Select(p => string.IsNullOrWhiteSpace(p.Name) ? "(未命名 API)" : p.Name)
            .ToList();
        if (missingKey.Count > 0)
            _tray.ShowBalloon("SDemo.QuickTranslate",
                $"还有 {missingKey.Count} 个 API 没填 Key：{string.Join("、", missingKey)}。请右键托盘图标 → 首选项 → 模型 进行设置。");

        Log.Info("启动完成");

        // 冒烟测试模式：验证启动流程后自动退出
        if (e.Args.Contains("--smoke"))
            Task.Delay(1000).ContinueWith(_ => Dispatcher.BeginInvoke(() => Shutdown()));

        // 诊断模式：连续两次捕获并记录日志，用于排查“只有第一次能捕获/翻译”的问题
        if (e.Args.Contains("--diag"))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1000);
                    var fg = Native.NativeWin.GetForegroundWindow();
                    uint fgPid = 0;
                    if (fg != IntPtr.Zero) Native.NativeWin.GetWindowThreadProcessId(fg, out fgPid);
                    Log.Info($"DIAG 前台窗口: hwnd={fg} pid={fgPid} title={Native.NativeWin.GetWindowTitle(fg)}");
                    var r1 = await Task.Run(() => SelectionCapture.Capture());
                    Log.Info($"DIAG 捕获1: kind={r1.Kind} textLen={r1.Text.Length} imgLen={r1.ImageBytes?.Length ?? 0}");
                    if (r1.Kind == CaptureKind.Text)
                    {
                        Log.Info($"DIAG 捕获1 文本: {Truncate(r1.Text)}");
                        if (r1.Text.Length > 0)
                            Log.Info($"DIAG 捕获1 hex: {Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes(r1.Text[..Math.Min(24, r1.Text.Length)]))}");
                    }
                    await Task.Delay(600);
                    var r2 = await Task.Run(() => SelectionCapture.Capture());
                    Log.Info($"DIAG 捕获2: kind={r2.Kind} textLen={r2.Text.Length} imgLen={r2.ImageBytes?.Length ?? 0}");
                    if (r2.Kind == CaptureKind.Text) Log.Info($"DIAG 捕获2 文本: {Truncate(r2.Text)}");
                }
                catch (Exception ex) { Log.Error(ex); }
                _ = Dispatcher.BeginInvoke(() => Shutdown());
            });
        }
    }

    private static string Truncate(string s) => s.Length <= 160 ? s : s[..160] + "…";

    /// <summary>
    /// 无界面自检（--selftest）：验证「关闭思考参数 / 流式增量 / 最大输出 Token」是否真的生效。
    /// 结果写入日志（%APPDATA%\SDemo\QuickTranslate\logs）与 %TEMP%\sdemo-selftest-result.txt，退出码 0=有正文，1=失败。
    /// </summary>
    private static async Task RunSelfTestAsync(string[] args)
    {
        string Arg(string name, string fallback = "")
        {
            var hit = args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
            return hit is null ? fallback : hit[(name.Length + 1)..];
        }

        bool reply = args.Any(a => a.Equals("--reply", StringComparison.OrdinalIgnoreCase));
        bool think = args.Any(a => a.Equals("--think", StringComparison.OrdinalIgnoreCase));
        string text = Arg("--text", reply
            ? "Could you send me the report by Friday?"
            : "Hello world. This is a SDemo.QuickTranslate self-test. 请把它翻译成目标语言。");
        string to = Arg("--to", "中文");
        bool skipReasoning = !think;

        // 自检默认走「首选项 → 模型」里的默认模型；解析不到时退回旧字段 api
        ApiSettings def;
        string defSource;
        try
        {
            var sel = Models.Resolve();
            def = sel.ToApiSettings();
            defSource = $"{sel.Display}（来源={sel.Source}）";
        }
        catch (Exception ex)
        {
            Log.Info($"SELFTEST 未能解析默认模型，改用旧字段：{ex.Message}");
            def = Settings.Api;
            defSource = "旧字段 api（默认模型未设置）";
        }
        Log.Info($"SELFTEST 默认模型: {defSource}");
        if (int.TryParse(Arg("--tokens"), out var tokens) && tokens > 0) def.MaxTokens = tokens;

        // 可用 --base/--model/--key 临时指向别的接口（不改动已保存的设置），便于试验新服务端
        var cfg = new ApiSettings
        {
            BaseUrl = Arg("--base", def.BaseUrl),
            ApiKey = Arg("--key", def.ApiKey),
            Model = Arg("--model", def.Model),
            Temperature = def.Temperature,
            MaxTokens = def.MaxTokens,
            TimeoutSeconds = def.TimeoutSeconds,
        };

        int exit = 1;
        try
        {
            var client = new OpenAIClient(cfg) { SkipReasoningForTranslation = skipReasoning };
            Log.Info($"SELFTEST 开始: 模式={(reply ? "生成回复" : "翻译")} 目标={to} 模型={cfg.Model} " +
                     $"地址={cfg.BaseUrl} 最大输出Token={cfg.MaxTokens} 关闭思考={skipReasoning} 输入长度={text.Length}");

            var sb = new StringBuilder();
            int blocks = 0, contentBlocks = 0, reasoningChars = 0;
            string? finish = null;
            int? used = null;
            var sw = Stopwatch.StartNew();
            long firstReasoning = -1, firstContent = -1, loggedChars = 0;

            var stream = reply
                ? client.GenerateReplyStreamAsync(text, string.IsNullOrWhiteSpace(to) ? null : to)
                : client.TranslateStreamAsync(text, to);

            await foreach (var d in stream)
            {
                if (d.FinishReason is not null) finish = d.FinishReason;
                if (d.CompletionTokens is int u) used = u;

                if (!string.IsNullOrEmpty(d.Reasoning))
                {
                    if (firstReasoning < 0)
                    {
                        firstReasoning = sw.ElapsedMilliseconds;
                        Log.Info($"SELFTEST 首个思考增量: {sw.ElapsedMilliseconds}ms 内容={Truncate(d.Reasoning)}");
                    }
                    reasoningChars += d.Reasoning.Length;
                    if (reasoningChars - loggedChars >= 500)
                    {
                        loggedChars = reasoningChars;
                        Log.Info($"SELFTEST 思考实时推进: 已 {reasoningChars} 字 / {sw.ElapsedMilliseconds}ms");
                    }
                }

                if (!string.IsNullOrEmpty(d.Content))
                {
                    if (firstContent < 0)
                    {
                        firstContent = sw.ElapsedMilliseconds;
                        Log.Info($"SELFTEST 首个正文增量（首字）: {sw.ElapsedMilliseconds}ms");
                    }
                    contentBlocks++;
                    sb.Append(d.Content);
                }
                blocks++;
            }

            Log.Info($"SELFTEST 结束: 块数={blocks} 正文块={contentBlocks} 首思考={firstReasoning}ms 首字={firstContent}ms " +
                     $"思考字数={reasoningChars} finish={finish ?? "?"} 用量={used?.ToString() ?? "?"} " +
                     $"结果长度={sb.Length} 总耗时={sw.ElapsedMilliseconds}ms");
            Log.Info($"SELFTEST 结果: {sb.ToString().Trim()}");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "sdemo-selftest-result.txt"), sb.ToString()); } catch { }

            exit = sb.Length > 0 ? 0 : 1;
            if (sb.Length == 0) Log.Info("SELFTEST 判定: 失败（没有拿到任何正文内容）");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            Log.Info($"SELFTEST 判定: 异常失败 - {ex.Message}");
        }

        var app = System.Windows.Application.Current;
        Environment.ExitCode = exit;
        app?.Dispatcher.BeginInvoke(() => app.Shutdown());
    }

    /// <summary>构建示例浮窗（--window=popup），用于检查翻译弹窗外观。</summary>
    private static Window BuildDemoPopup()
    {
        var w = new PopupWindow();
        w.ShowLoading("翻译为中文", 500, 320);
        w.SetOriginal(new CaptureResult { Kind = CaptureKind.Text, Text = "Hello world, this is a demo." });
        w.BeginStream("翻译为中文");
        w.AppendReasoningChunk("思考过程是实时增量追加的：可以边思考边显示进度，而不是最后一次性贴出来。");
        w.AppendResultChunk("你好，世界。这是用于检查浮窗外观的示例结果。");
        w.SetNotice("已达到「最大输出 Token」上限（当前 2048）：输出被接口截断（思考过程也算在这个额度里）。" +
                    "可在首选项 → 模型 里调大该值。");
        return w;
    }

    /// <summary>构建示例「输入翻译」窗口（--window=input），用于检查界面外观与截图。</summary>
    private static Window BuildDemoInputWindow()
    {
        var w = new InputTranslateWindow(new HotkeyEntry
        {
            Name = "输入翻译",
            Action = "InputTranslate",
            TargetLanguage = "English",
        });
        w.ShowDemo(
            input: "人工智能正在改变我们工作和学习的方式。",
            reasoning: "先确认目标语言是英文，再把中文句子按意群拆开，注意“正在改变”用现在进行时。",
            result: "Artificial intelligence is changing the way we work and learn.\n" +
                    "（示例译文：输入框里的内容翻译后逐字显示在这里。）");
        return w;
    }

    private async void OnHotkey(HotkeyEntry entry)
    {
        // 忙碌时不再静默忽略：用户再按热键通常就是想要“立刻翻译这一次”，
        // 所以先打断上一次（关掉它的弹窗/流），再执行新的。
        if (_busy)
            Log.Info($"上一次操作仍在进行，打断并执行新热键: {entry.Name}");

        await RunExclusive(async () => await Controller.ExecuteAsync(entry));
    }

    private async void OnTrayAction(string action)
    {
        switch (action)
        {
            case "prefs": ShowPreferences(); break;
            case "help": new AboutWindow().Show(); break;
            case "restart": Restart(); break;
            case "exit": Shutdown(); break;
            case "ocr":
                await ExecuteTransient("截图翻译", "ScreenshotOcr", "中文");
                break;
            case "input":
                await ExecuteTransient("输入翻译", "InputTranslate", "");
                break;
            case "reply":
                await ExecuteTransient("生成回复", "GenerateReply", "");
                break;
            default:
                if (action.StartsWith("translate:"))
                {
                    var lang = action["translate:".Length..];
                    await ExecuteTransient($"翻译为{lang}", "Translate", lang);
                }
                break;
        }
    }

    private async Task ExecuteTransient(string name, string action, string lang)
    {
        await RunExclusive(async () => await Controller.ExecuteAsync(
            new HotkeyEntry { Name = name, Action = action, TargetLanguage = lang, Enabled = true }));
    }

    /// <summary>
    /// 串行化执行：同时只跑一次捕获+翻译。若上一次还在跑，先取消（打断）并等它收尾，再执行新的，
    /// 避免两次捕获/两个弹窗互相打架（表现为“按了热键没反应”）。
    /// </summary>
    private async Task RunExclusive(Func<Task> work)
    {
        if (_running is { IsCompleted: false })
        {
            Log.Info("打断上一次操作，改为执行新的热键");
            Controller.CancelCurrent();
            try { await _running; } catch (Exception ex) { Log.Error(ex); }
        }
        _busy = true;
        var task = Run();
        _running = task;
        try { await task; }
        finally { _busy = false; _running = null; }

        async Task Run()
        {
            try { await work(); }
            catch (Exception ex) { Log.Error(ex); }
        }
    }

    private void ShowPreferences()
    {
        // 首选项只允许开一个：已经开着就把现有窗口提到前面。
        if (_prefsWindow != null)
        {
            if (_prefsWindow.WindowState == WindowState.Minimized)
                _prefsWindow.WindowState = WindowState.Normal;
            _prefsWindow.Activate();
            return;
        }

        var w = new PreferencesWindow { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        w.Closed += (_, _) => _prefsWindow = null;
        _prefsWindow = w;
        w.Show();
        w.Activate();
    }

    /// <summary>
    /// 暂停全局热键：首选项窗口打开期间必须暂停，否则录入热键时会触发已有热键，
    /// 而且 RegisterHotKey 已占用的组合也无法被其它程序/录入框正常响应。
    /// </summary>
    public static void SuspendHotkeys()
    {
        if (Current is not App app || app._hotkeysSuspended) return;
        app._hotkeysSuspended = true;
        try
        {
            Hotkeys?.UnregisterAll();
            Log.Info("全局热键已暂停（首选项窗口打开）");
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    /// <summary>恢复全局热键注册（首选项窗口关闭后）。</summary>
    public static void ResumeHotkeys()
    {
        if (Current is not App app || !app._hotkeysSuspended) return;
        app._hotkeysSuspended = false;
        try
        {
            var errs = Hotkeys?.RegisterAll(Settings) ?? new List<string>();
            Log.Info($"全局热键已恢复（{Settings.Hotkeys.Count(h => h.Enabled && h.KeyVk != 0)} 条）");
            if (errs.Count > 0)
                app._tray?.ShowBalloon("热键注册提示", string.Join("\n", errs), warning: true);
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    private void Restart()
    {
        try
        {
            Hotkeys?.Dispose();
            Hotkeys = null;
            _tray?.Dispose();
            try { _mutex?.ReleaseMutex(); } catch { }
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            Process.Start(psi);
        }
        catch (Exception ex) { Log.Error(ex); }
        Shutdown();
    }

    /// <summary>设置变化后应用：让模型客户端缓存失效、重建热键与托盘菜单。</summary>
    public void ApplySettings()
    {
        // 地址 / Key / 模型 / 参数都可能变了：清掉按「API + 模型」缓存的客户端，下次请求按新设置重建
        Models.Clear();
        var errs = Hotkeys?.RegisterAll(Settings) ?? new List<string>();
        _tray?.RebuildMenu(Settings);
        ApplyAutoStart(Settings.General.AutoStart, quiet: false);
        if (errs.Count > 0)
            _tray?.ShowBalloon("热键注册提示", string.Join("\n", errs), warning: true);
    }

    private static void ApplyAutoStart(bool enable, bool quiet)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (enable)
                key.SetValue("SDemo.QuickTranslate", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("SDemo.QuickTranslate", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            if (!quiet) MessageBox.Show("设置开机自启失败：" + ex.Message, "SDemo.QuickTranslate");
            Log.Error(ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Hotkeys?.Dispose();
        Hotkeys = null;
        _tray?.Dispose();
        try { Models?.Clear(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
