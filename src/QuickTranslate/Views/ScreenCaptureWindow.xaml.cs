using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QuickTranslate.Native;

namespace QuickTranslate.Views;

/// <summary>全屏框选遮罩：拖拽选择屏幕区域，用于「截图翻译」。Esc / 右键取消。</summary>
public partial class ScreenCaptureWindow : Window
{
    /// <summary>用户选择的屏幕区域（物理像素）；取消为 null。</summary>
    public System.Drawing.Rectangle? Result { get; private set; }

    private Point _start;
    private bool _dragging;
    private Rect _sel;

    private ScreenCaptureWindow()
    {
        InitializeComponent();

        // 覆盖整个虚拟桌面（DIP）
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        FullRect.Rect = new Rect(0, 0, Math.Max(1, Width), Math.Max(1, Height));

        SelBorder.HorizontalAlignment = HorizontalAlignment.Left;
        SelBorder.VerticalAlignment = VerticalAlignment.Top;
        SizeTag.HorizontalAlignment = HorizontalAlignment.Left;
        SizeTag.VerticalAlignment = VerticalAlignment.Top;

        Loaded += (_, _) => { Activate(); Focus(); };
    }

    /// <summary>弹出框选遮罩并等待用户选择；返回物理像素区域，取消返回 null。</summary>
    public static System.Drawing.Rectangle? PickRegion()
    {
        var w = new ScreenCaptureWindow();
        w.ShowDialog();
        return w.Result;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Result = null;
            e.Handled = true;
            Close();
        }
    }

    private void Window_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        Result = null;
        Close();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Root);
        _dragging = true;
        HintCard.Visibility = Visibility.Collapsed;
        SelBorder.Visibility = Visibility.Visible;
        SizeTag.Visibility = Visibility.Visible;
        CaptureMouse();
        UpdateSelection(_start);
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging) UpdateSelection(e.GetPosition(Root));
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        if (_sel.Width < 4 || _sel.Height < 4)   // 误点一下 = 取消
        {
            Result = null;
            Close();
            return;
        }
        Result = ToPhysical(_sel);
        Close();
    }

    private void UpdateSelection(Point p)
    {
        _sel = new Rect(
            Math.Min(_start.X, p.X),
            Math.Min(_start.Y, p.Y),
            Math.Abs(p.X - _start.X),
            Math.Abs(p.Y - _start.Y));

        HoleRect.Rect = _sel;

        SelBorder.Width = _sel.Width;
        SelBorder.Height = _sel.Height;
        SelBorder.Margin = new Thickness(_sel.X, _sel.Y, 0, 0);

        SizeText.Text = $"{_sel.Width:0} × {_sel.Height:0}";
        SizeTag.Margin = new Thickness(_sel.X, _sel.Y + _sel.Height + 6, 0, 0);
    }

    /// <summary>DIP（窗口坐标）→ 物理像素（屏幕坐标）。</summary>
    private static System.Drawing.Rectangle ToPhysical(Rect r)
    {
        double k = 1.0 / NativeWin.GetDpiScaleSafe();   // DIP → 物理像素

        int vx = (int)Math.Floor(SystemParameters.VirtualScreenLeft * k);
        int vy = (int)Math.Floor(SystemParameters.VirtualScreenTop * k);
        int vw = (int)Math.Ceiling(SystemParameters.VirtualScreenWidth * k);
        int vh = (int)Math.Ceiling(SystemParameters.VirtualScreenHeight * k);

        int x = (int)Math.Round((SystemParameters.VirtualScreenLeft + r.X) * k);
        int y = (int)Math.Round((SystemParameters.VirtualScreenTop + r.Y) * k);
        int w = (int)Math.Round(r.Width * k);
        int h = (int)Math.Round(r.Height * k);

        // 夹紧到虚拟桌面范围
        x = Math.Max(x, vx);
        y = Math.Max(y, vy);
        w = Math.Min(w, vx + vw - x);
        h = Math.Min(h, vy + vh - y);
        return new System.Drawing.Rectangle(x, y, Math.Max(1, w), Math.Max(1, h));
    }
}
