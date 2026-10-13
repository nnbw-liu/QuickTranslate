using System.Diagnostics;
using System.Windows;
using QuickTranslate.Models;
using QuickTranslate.Services;
using QuickTranslate.Views;

namespace QuickTranslate;

/// <summary>应用入口：无主窗口，启动即托盘后台运行。</summary>
public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static SettingsService SettingsService { get; private set; } = null!;
    public static OpenAIClient Api { get; private set; } = null!;
    public static TranslateController Controller { get; private set; } = null!;
    /// <summary>全局热键管理器（首选项窗口打开期间会被暂停）。</summary>
    public static HotkeyManager? Hotkeys { get; private set; }

    private Mutex? _mutex;
    private TrayIconService? _tray;
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

        // 界面调试/截图模式（--window=prefs|help|popup）：只打开指定窗口，
        // 不占用单实例互斥量、不注册托盘图标与热键；关闭窗口即退出。
        var windowArg = e.Args.FirstOrDefault(a => a.StartsWith("--window=", StringComparison.OrdinalIgnoreCase));
        if (windowArg != null)
        {
            Window w = windowArg["--window=".Length..].ToLowerInvariant() switch
            {
                "help" => new AboutWindow(),
                "popup" => BuildDemoPopup(),
                _ => new PreferencesWindow(),
            };
            w.Closed += (_, _) => Shutdown();
            w.Show();
            w.Activate();
            return;
        }

        // 单实例
        _mutex = new Mutex(true, @"Local\QuickTranslate.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("QuickTranslate 已经在运行了。", "QuickTranslate",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ApplyAutoStart(Settings.General.AutoStart, quiet: true);
        Api = new OpenAIClient(Settings.Api)
        {
            SkipReasoningForTranslation = Settings.General.SkipReasoningForTranslation,
        };
        Controller = new TranslateController();

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

        if (string.IsNullOrWhiteSpace(Settings.Api.ApiKey))
            _tray.ShowBalloon("QuickTranslate", "尚未配置 OpenAI API Key，请右键托盘图标 → 首选项 进行设置。");

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

    /// <summary>构建示例浮窗（--window=popup），用于检查翻译弹窗外观。</summary>
    private static Window BuildDemoPopup()
    {
        var w = new PopupWindow();
        w.ShowLoading("翻译为中文", 500, 320);
        w.SetOriginal(new CaptureResult { Kind = CaptureKind.Text, Text = "Hello world, this is a demo." });
        w.BeginStream("翻译为中文");
        w.AppendResultChunk("你好，世界。这是用于检查浮窗外观的示例结果。");
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
        var w = new PreferencesWindow { WindowStartupLocation = WindowStartupLocation.CenterScreen };
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

    /// <summary>设置变化后应用：重建 API 客户端、热键与托盘菜单。</summary>
    public void ApplySettings()
    {
        Api = new OpenAIClient(Settings.Api)
        {
            SkipReasoningForTranslation = Settings.General.SkipReasoningForTranslation,
        };
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
                key.SetValue("QuickTranslate", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("QuickTranslate", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            if (!quiet) MessageBox.Show("设置开机自启失败：" + ex.Message, "QuickTranslate");
            Log.Error(ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Hotkeys?.Dispose();
        Hotkeys = null;
        _tray?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
