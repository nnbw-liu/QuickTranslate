using System.Windows;
using System.Windows.Input;
using SDemo.QuickTranslate.Models;
using SDemo.QuickTranslate.Services;

namespace SDemo.QuickTranslate.Views;

/// <summary>
/// 「输入翻译」窗口：按热键弹出输入框，填入内容后（可选目标语言）翻译。
/// 特点：
/// ① <see cref="Window.Topmost"/> = true，始终置顶，不会被其它窗口盖住；
/// ② 目标语言可随时切换/直接输入；
/// ③ <c>Esc</c> 关闭窗口并**立刻中断**正在进行的推理（等价于点「停止」）；
/// ④ <c>Ctrl+Enter</c> 直接翻译；译文与思考过程都是流式逐字显示。
/// </summary>
public partial class InputTranslateWindow : Window, IStreamSink
{
    /// <summary>常见目标语言候选（下拉框可编辑，也可以直接输入别的语言）。</summary>
    private static readonly string[] CommonLanguages =
    {
        "中文", "English", "日本語", "한국어", "繁體中文", "Français", "Deutsch",
        "Español", "Русский", "Português", "Italiano", "Tiếng Việt", "ไทย", "العربية",
    };

    private const string TranslateButtonText = "翻译 (Ctrl+Enter)";
    private const string StopButtonText = "停止";

    private CancellationTokenSource? _cts;
    private IntPtr _prevForeground;
    private bool _streaming;

    /// <summary>触发本窗口的那条热键（决定标题、预置语言与其专用提示词）。</summary>
    public HotkeyEntry? Entry { get; private set; }

    /// <summary>是否正在流式生成中。</summary>
    public bool IsStreaming => _streaming;

    public InputTranslateWindow() : this(null) { }

    public InputTranslateWindow(HotkeyEntry? entry)
    {
        InitializeComponent();
        foreach (var l in CommonLanguages) LangBox.Items.Add(l);

        // 关闭时先中断流、再把焦点还给用户原来的程序，避免影响下一次「选中即翻译」的捕获
        _prevForeground = Native.NativeWin.GetForegroundWindow();
        Closed += (_, _) =>
        {
            CancelCurrent();
            RestoreFocus();
        };
        Loaded += (_, _) => FocusInput();

        ApplyEntry(entry);
    }

    /// <summary>换一条热键触发时更新标题与预置语言（窗口被复用时调用）。</summary>
    public void ApplyEntry(HotkeyEntry? entry)
    {
        Entry = entry;
        TitleText.Text = string.IsNullOrWhiteSpace(entry?.Name) ? "输入翻译" : entry!.Name;

        // 优先级：该热键自己的目标语言 > 上次用过的语言 > 中文
        var preset = !string.IsNullOrWhiteSpace(entry?.TargetLanguage)
            ? entry!.TargetLanguage
            : App.Settings.General.LastInputTargetLanguage;
        SetLanguage(string.IsNullOrWhiteSpace(preset) ? "中文" : preset);
    }

    public void SetLanguage(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return;
        if (!LangBox.Items.Contains(lang)) LangBox.Items.Add(lang);
        LangBox.Text = lang;
    }

    /// <summary>把键盘焦点放到输入框（热键唤起后可以直接打字）。</summary>
    public void FocusInput()
    {
        try { InputBox.Focus(); Keyboard.Focus(InputBox); } catch { }
    }

    /// <summary>界面演示/截图用：填充输入、思考与译文。</summary>
    public void ShowDemo(string input, string reasoning, string result)
    {
        InputBox.Text = input;
        if (!string.IsNullOrEmpty(reasoning))
        {
            ReasoningPanel.Visibility = Visibility.Visible;
            ReasoningText.Text = reasoning;
            ResultText.Margin = new Thickness(0, 10, 0, 0);
        }
        ResultText.Text = result;
        SetStatus("翻译完成（演示）");
        SetModel("演示 API / 示例模型");
        SetNotice("已达到「最大输出 Token」上限（当前 2048）：输出被接口截断（思考过程也算在这个额度里）。" +
                  "可在首选项 → 模型 里调大该值。");
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    // ---------- 翻译 ----------

    private async void Translate_Click(object sender, RoutedEventArgs e) => await RunTranslateAsync();

    private async Task RunTranslateAsync()
    {
        // 生成中点一次 = 主动打断（与 Esc 等效）
        if (_streaming)
        {
            CancelCurrent();
            return;
        }

        var text = InputBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("请先输入要翻译的内容");
            FocusInput();
            return;
        }

        var lang = CurrentLanguage;
        RememberLanguage(lang);

        using var cts = new CancellationTokenSource();
        _cts = cts;
        BeginStream();

        try
        {
            await TranslateController.TranslateTextIntoAsync(this, text, lang, TranslateController.TranslatePrompt(Entry), Entry, cts.Token);
            SetStatus(ResultLength > 0 ? $"翻译完成（{ResultLength} 字）" : "没有拿到译文");
            HintText.Text = $"已翻译为 {lang}。可修改内容后再次翻译，或按 Esc 关闭。";
        }
        catch (OperationCanceledException)
        {
            Log.Info("输入翻译已中断（Esc / 停止 / 新请求）");
            SetStatus("已中断");
            HintText.Text = "已中断本次推理（已保留已生成的部分）。";
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            SetStatus("翻译失败");
            ShowError("出错了：\n" + ex.Message);
            HintText.Text = "翻译失败，可检查首选项里的接口地址 / API Key / 模型。";
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            EndStream();
        }
    }

    /// <summary>中断当前正在进行的推理（Esc、点「停止」、关闭窗口、新热键都会调用）。</summary>
    public void CancelCurrent()
    {
        try { _cts?.Cancel(); } catch (Exception ex) { Log.Error(ex); }
    }

    private string CurrentLanguage => string.IsNullOrWhiteSpace(LangBox.Text) ? "中文" : LangBox.Text.Trim();

    /// <summary>记住本次目标语言，下次打开窗口时预选（写进设置，重启后依然有效）。</summary>
    private static void RememberLanguage(string lang)
    {
        try
        {
            if (string.Equals(App.Settings.General.LastInputTargetLanguage, lang, StringComparison.Ordinal)) return;
            App.Settings.General.LastInputTargetLanguage = lang;
            App.SettingsService.Save(App.Settings);
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    // ---------- IStreamSink ----------

    public void SetStatus(string s) => StatusText.Text = s;

    /// <summary>显示本次请求实际使用的「API / 模型」（多 API 时一眼看出走的是哪个）。</summary>
    public void SetModel(string providerModel)
    {
        if (string.IsNullOrWhiteSpace(providerModel))
        {
            ModelText.Visibility = Visibility.Collapsed;
            return;
        }
        ModelText.Text = "模型：" + providerModel;
        ModelText.ToolTip = ModelText.Text;
        ModelText.Visibility = Visibility.Visible;
    }

    public int ResultLength => ResultText.Text.Length;

    public bool HasNotice => NoticeText.Visibility == Visibility.Visible;

    public void SetNotice(string text)
    {
        NoticeText.Text = text;
        NoticeText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void AppendResultChunk(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        ResultText.AppendText(chunk);
        Scroll.ScrollToEnd();
    }

    public void AppendReasoningChunk(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        if (ReasoningPanel.Visibility != Visibility.Visible)
        {
            ReasoningPanel.Visibility = Visibility.Visible;
            ResultText.Margin = new Thickness(0, 10, 0, 0);
        }
        ReasoningText.AppendText(chunk);
        Scroll.ScrollToEnd();
    }

    public void ShowError(string msg)
    {
        if (ResultText.Text.Length > 0) ResultText.AppendText("\n\n[错误] " + msg);
        else ResultText.Text = msg;
        Scroll.ScrollToEnd();
    }

    // ---------- 界面状态 ----------

    private void BeginStream()
    {
        _streaming = true;
        SetStatus("正在生成…");
        Busy.Visibility = Visibility.Visible;
        ResultText.Text = "";
        ReasoningText.Text = "";
        ReasoningPanel.IsExpanded = true;
        ReasoningHeader.Text = "思考过程（实时）";
        ReasoningPanel.Visibility = Visibility.Collapsed;
        ResultText.Margin = new Thickness(0);
        ClearNotice();
        TranslateBtn.Content = StopButtonText;
        TranslateBtn.ToolTip = "停止本次推理（也可直接按 Esc）";
        CopyBtn.IsEnabled = false;
        ClearBtn.IsEnabled = false;
        HintText.Text = "正在翻译…按 Esc 可随时中断并关闭。";
        Scroll.ScrollToEnd();
    }

    private void EndStream()
    {
        _streaming = false;
        Busy.Visibility = Visibility.Collapsed;
        ReasoningHeader.Text = "思考过程";   // 流结束，去掉「（实时）」
        TranslateBtn.Content = TranslateButtonText;
        TranslateBtn.ToolTip = "翻译输入的内容（Ctrl+Enter）";
        CopyBtn.IsEnabled = ResultLength > 0;
        ClearBtn.IsEnabled = true;
    }

    private void ClearNotice()
    {
        NoticeText.Text = "";
        NoticeText.Visibility = Visibility.Collapsed;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_streaming) return;
        InputBox.Text = "";
        ResultText.Text = "";
        ReasoningText.Text = "";
        ReasoningPanel.Visibility = Visibility.Collapsed;
        ReasoningPanel.IsExpanded = true;
        ReasoningHeader.Text = "思考过程（实时）";
        ResultText.Margin = new Thickness(0);
        ClearNotice();
        StatusText.Text = "";
        HintText.Text = "提示：翻译过程中按 Esc（或点「停止」）会立刻中断本次推理并关闭窗口。";
        FocusInput();
    }

    private void CopyResult_Click(object sender, RoutedEventArgs e) => CopyResult();

    private void CopyResult()
    {
        try
        {
            var txt = ResultText.SelectedText.Length > 0 ? ResultText.SelectedText : ResultText.Text;
            if (!string.IsNullOrEmpty(txt)) System.Windows.Clipboard.SetText(txt);
            SetStatus("译文已复制到剪贴板");
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- 键与焦点 ----------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc：关闭窗口，并立刻中断正在进行的推理
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelCurrent();
            Close();
            return;
        }

        // Ctrl+Enter：直接开始翻译（文本框里回车仍然是换行）
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            _ = RunTranslateAsync();
            return;
        }

        // Ctrl+Shift+C：复制译文（避免与文本框自身的 Ctrl+C 冲突）
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            CopyResult();
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }

    /// <summary>关闭时把焦点还给之前的窗口，保证下一次「选中即翻译」的捕获正常。</summary>
    private void RestoreFocus()
    {
        try
        {
            if (_prevForeground != IntPtr.Zero && Native.NativeWin.IsWindow(_prevForeground))
                Native.NativeWin.SetForegroundWindow(_prevForeground);
        }
        catch { }
    }
}
