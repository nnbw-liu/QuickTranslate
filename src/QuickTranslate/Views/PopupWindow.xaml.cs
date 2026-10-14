using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using QuickTranslate.Models;
using QuickTranslate.Services;

namespace QuickTranslate.Views;

/// <summary>翻译结果浮窗：流式显示译文与思考过程（实现 <see cref="IStreamSink"/> 供 TranslateController 写入）。</summary>
public partial class PopupWindow : Window, IStreamSink
{
    public HotkeyEntry? Entry { get; private set; }
    public CaptureResult? Capture { get; private set; }

    /// <summary>动作请求：action 为 "translate:语言" / "retranslate" / "reply" / "ocr" / "close"</summary>
    public event Action<PopupWindow, string>? RequestAction;

    private int _cursorX, _cursorY;
    private IntPtr _prevForeground;

    /// <summary>是否正在流式生成中（防止同一弹窗并发流式/操作）。</summary>
    public bool IsStreaming { get; private set; }

    public PopupWindow()
    {
        InitializeComponent();
        // 关闭时把焦点还给之前的程序，避免影响下一次捕获
        Closed += (_, _) => RestoreFocus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            CopyResult();
            e.Handled = true;
        }
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    // ---------- 状态切换 ----------

    public void ShowLoading(string title, int cx, int cy)
    {
        Entry = null; Capture = null;
        _prevForeground = Native.NativeWin.GetForegroundWindow(); // 记录前台，关闭时恢复
        TitleText.Text = title;
        StatusText.Text = "处理中…";
        Busy.Visibility = Visibility.Visible;
        ResultText.Text = "";
        OriginalExpander.Visibility = Visibility.Collapsed;
        OriginalExpander.Header = "原文";
        OriginalText.Text = "";
        ClearOriginalImage();
        CopyBtn.IsEnabled = RetranslateBtn.IsEnabled = ReplyBtn.IsEnabled = false;
        OcrBtn.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Collapsed;
        SetModel("");   // 模型标签由 Controller 在解析出本次的 API + 模型后填入
        ClearNotice();
        Show(); // ShowActivated=False：显示时不抢焦点，避免干扰捕获/用户程序
        Position(cx, cy);
    }

    public void SetOriginal(CaptureResult cap)
    {
        Capture = cap;

        // 有图就把图放出来（截图翻译 / 图片翻译）：译文对不对、模型有没有真的看到图，一眼可辨。
        if (cap.ImageBytes is { Length: > 0 }) ShowOriginalImage(cap.ImageBytes);

        if (cap.Kind == CaptureKind.Text)
        {
            OriginalExpander.Header = "原文";
            OriginalText.Text = cap.Text;
            OriginalExpander.Visibility = Visibility.Visible;
        }
        else if (cap.Kind == CaptureKind.Image)
        {
            // 图片模式下没有可展示的“原文”文本：先把原文区收起，
            // 若随后走了本地 OCR，会由 SetOriginalText 重新填上识别结果。
            OriginalExpander.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 单独填入「原文」文本（例如本地 OCR 的识别结果），**不影响**已展示的原图与 <see cref="Capture"/>。
    /// 这样 OCR 回退后用户仍能看到原图，也能看出译文是基于识别文本得到的。
    /// </summary>
    public void SetOriginalText(string text, string header = "原文（本地 OCR 识别）")
    {
        OriginalExpander.Header = header;
        OriginalText.Text = text;
        OriginalExpander.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>把 PNG 字节解码成可冻结的位图（源流立即释放，避免长期占用内存）。</summary>
    private static BitmapSource? DecodeImage(byte[] png)
    {
        try
        {
            using var ms = new MemoryStream(png);
            var frame = BitmapFrame.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
    }

    private void ShowOriginalImage(byte[] png)
    {
        var frame = DecodeImage(png);
        if (frame == null) return;
        OriginalImage.Source = frame;
        OriginalImagePanel.Visibility = Visibility.Visible;
        CopyImageMenuItem.Visibility = Visibility.Visible;
    }

    private void ClearOriginalImage()
    {
        OriginalImage.Source = null;
        OriginalImagePanel.Visibility = Visibility.Collapsed;
        CopyImageMenuItem.Visibility = Visibility.Collapsed;
    }

    public void SetStatus(string s) => StatusText.Text = s;

    /// <summary>显示本次请求实际使用的「API / 模型」。</summary>
    public void SetModel(string providerModel)
    {
        if (string.IsNullOrWhiteSpace(providerModel))
        {
            ModelText.Visibility = Visibility.Collapsed;
            return;
        }
        ModelText.Text = "模型：" + providerModel;
        ModelText.Visibility = Visibility.Visible;
    }

    /// <summary>在浮窗底部显示一条常驻提示（如「已达到最大输出 Token 上限」），不会被 EndStream 清掉。</summary>
    public void SetNotice(string text)
    {
        NoticeText.Text = text;
        NoticeText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ClearNotice()
    {
        NoticeText.Text = "";
        NoticeText.Visibility = Visibility.Collapsed;
    }

    /// <summary>当前是否显示了提示条（如「已达最大输出 Token 上限」）。</summary>
    public bool HasNotice => NoticeText.Visibility == Visibility.Visible;

    // ---------- 流式输出 ----------

    public int ResultLength => ResultText.Text.Length;

    /// <summary>开始流式展示：清空正文/思考区，保持加载动画，直到 EndStream 结束。</summary>
    public void BeginStream(string title)
    {
        // 注意：不要在这里清空 Entry —— 弹窗动作（重新翻译/生成回复）需要沿用它拿到语言与提示词，
        // Entry 由 ShowLoading（新一轮）和 EndStream（本轮结束）负责设置。
        IsStreaming = true;
        TitleText.Text = title;
        StatusText.Text = "正在生成…";
        Busy.Visibility = Visibility.Visible;
        ResultText.Text = "";
        ReasoningPanel.Visibility = Visibility.Collapsed;
        ReasoningText.Text = "";
        ResultText.Margin = new Thickness(0, 4, 0, 0);
        CopyBtn.IsEnabled = RetranslateBtn.IsEnabled = ReplyBtn.IsEnabled = false;
        OcrBtn.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Collapsed;
        ClearNotice();
        RepositionToSelectionOrCursor();
        Activate(); // 捕获已完成，此时可安全获得焦点：ESC 可取消流式，且下次热键关闭时会还原焦点
        Scroll.ScrollToEnd();
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
            ResultText.Margin = new Thickness(0, 12, 0, 0);
        }
        ReasoningText.AppendText(chunk);
        Scroll.ScrollToEnd();
    }

    public void EndStream(HotkeyEntry entry)
    {
        Entry = entry;
        IsStreaming = false;
        Busy.Visibility = Visibility.Collapsed;
        // 有提示条时保留当前状态文案（例如「模型只输出了思考过程」），否则恢复为热键名；
        // 否则 SetStatus 写的说明会被这里覆盖掉，用户什么也看不到。
        if (!HasNotice) StatusText.Text = entry.Name;
        CopyBtn.IsEnabled = RetranslateBtn.IsEnabled = ReplyBtn.IsEnabled = true;
        OcrBtn.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Visible;
        Scroll.ScrollToEnd();
        Activate();
        ResultText.Focus();
    }

    /// <summary>清空正文与思考区（用于「视觉模型失败 → 回退本地 OCR」等重试场景）。</summary>
    public void ClearResult()
    {
        ResultText.Text = "";
        ReasoningText.Text = "";
        ReasoningPanel.Visibility = Visibility.Collapsed;
        ResultText.Margin = new Thickness(0, 4, 0, 0);
        ClearNotice();
    }

    public void ShowResult(string text, HotkeyEntry entry)
    {
        BeginStream(entry.Name);
        ResultText.Text = text;
        Scroll.ScrollToEnd();
        EndStream(entry);
    }

    public void ShowNoSelection(int cx, int cy, HotkeyEntry entry)
    {
        IsStreaming = false;
        Entry = entry;
        Busy.Visibility = Visibility.Collapsed;
        ResultText.Text = "未检测到选中的文本或图片。\n\n请先在其它程序中选中内容，再按热键；\n或点击下方“截图翻译”，框选屏幕区域进行识别。";
        OcrBtn.Visibility = Visibility.Visible;
        CopyBtn.IsEnabled = RetranslateBtn.IsEnabled = ReplyBtn.IsEnabled = false;
        HintText.Visibility = Visibility.Collapsed;
        Position(cx, cy);
        Activate();
    }

    public void ShowError(string msg)
    {
        IsStreaming = false;
        Busy.Visibility = Visibility.Collapsed;
        StatusText.Text = "出错了";   // 不回退：错误直接把状态与原因都写出来
        if (ResultText.Text.Length > 0)
            ResultText.AppendText("\n\n[错误] " + msg);
        else
            ResultText.Text = msg;
        HintText.Visibility = Visibility.Collapsed;
        Position(_cursorX, _cursorY);
        Activate();
    }

    /// <summary>关闭时把焦点还给之前的窗口，保证下一次热键捕获正常。</summary>
    private void RestoreFocus()
    {
        try
        {
            if (_prevForeground != IntPtr.Zero && Native.NativeWin.IsWindow(_prevForeground))
                Native.NativeWin.SetForegroundWindow(_prevForeground);
        }
        catch { }
    }

    // ---------- 定位 ----------

    private void RepositionToSelectionOrCursor()
    {
        if (Capture != null && Capture.SelectionRect.Width > 0 && Capture.SelectionRect.Height > 0)
        {
            int cx = Capture.SelectionRect.Left + Capture.SelectionRect.Width / 2;
            int cy = Capture.SelectionRect.Top + Capture.SelectionRect.Height / 2;
            Position(cx, cy);
        }
        else
        {
            Position(_cursorX, _cursorY);
        }
    }

    /// <summary>将窗口定位到光标附近：默认上方，空间不足则偏移到下方，再夹紧到屏幕工作区内。</summary>
    private void Position(int cursorX, int cursorY)
    {
        _cursorX = cursorX; _cursorY = cursorY;
        UpdateLayout();

        double scale = Native.NativeWin.GetDpiScaleSafe(); // 物理像素 → DIP
        double w = ActualWidth > 0 ? ActualWidth : 560;
        double h = ActualHeight > 0 ? ActualHeight : 140;

        var scr = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursorX, cursorY));
        double left = scr.WorkingArea.Left * scale;
        double top = scr.WorkingArea.Top * scale;
        double right = scr.WorkingArea.Right * scale;
        double bottom = scr.WorkingArea.Bottom * scale;

        double x = cursorX * scale - w / 2;
        x = Math.Clamp(x, left + 6, Math.Max(left + 6, right - w - 6));

        double y = cursorY * scale - h - 16; // 默认上方
        if (y < top + 4) y = cursorY * scale + 18; // 空间不足 → 下方
        y = Math.Clamp(y, top + 4, Math.Max(top + 4, bottom - h - 4));

        Left = x;
        Top = y;
    }

    // ---------- 复制 ----------

    private void CopyResult()
    {
        try
        {
            var txt = ResultText.SelectedText.Length > 0 ? ResultText.SelectedText : ResultText.Text;
            if (!string.IsNullOrEmpty(txt)) System.Windows.Clipboard.SetText(txt);
        }
        catch { }
    }

    private void CopyOriginal()
    {
        // 优先复制界面上真正展示的「原文」：图片 + 本地 OCR 时，原文就是那段识别结果
        var text = OriginalText.Text.Length > 0 ? OriginalText.Text : Capture?.Text ?? "";
        if (text.Length == 0) return;
        try { System.Windows.Clipboard.SetText(text); } catch { }
    }

    /// <summary>复制原图到剪贴板（截图翻译 / 图片翻译时可用）。</summary>
    private void CopyOriginalImage()
    {
        if (Capture?.ImageBytes is not { Length: > 0 } bytes) return;
        var frame = DecodeImage(bytes);
        if (frame == null) return;
        try { System.Windows.Clipboard.SetImage(frame); } catch (Exception ex) { Log.Error(ex); }
    }

    // ---------- 事件处理 ----------

    private void CopyResult_Click(object sender, RoutedEventArgs e) => CopyResult();

    private void CopyOriginal_Click(object sender, RoutedEventArgs e) => CopyOriginal();

    private void CopyOriginalImage_Click(object sender, RoutedEventArgs e) => CopyOriginalImage();

    private void TranslateTo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is string lang && Capture != null)
            RequestAction?.Invoke(this, "translate:" + lang);
    }

    private void Retranslate_Click(object sender, RoutedEventArgs e) => RequestAction?.Invoke(this, "retranslate");

    private void Reply_Click(object sender, RoutedEventArgs e) => RequestAction?.Invoke(this, "reply");

    private void Ocr_Click(object sender, RoutedEventArgs e) => RequestAction?.Invoke(this, "ocr");

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
