using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SDemo.QuickTranslate.Models;
using SDemo.QuickTranslate.Services;

namespace SDemo.QuickTranslate.Views;

/// <summary>
/// 全屏框选遮罩：唤醒时先把整屏画面冻结显示出来，用户在这张冻结图上拖拽选择区域，
/// 确认后直接从冻结图裁剪，用于「截图翻译」。Esc / 右键取消。
/// </summary>
public partial class ScreenCaptureWindow : Window
{
    /// <summary>用户选择的屏幕区域（物理像素）；取消为 null。</summary>
    public System.Drawing.Rectangle? Result { get; private set; }

    /// <summary>冻结图对应的虚拟桌面范围（物理像素），用于坐标换算。</summary>
    private readonly System.Drawing.Rectangle _screen;

    private Point _start;
    private bool _dragging;
    private Rect _sel;

    private ScreenCaptureWindow(System.Drawing.Bitmap? frozen, System.Drawing.Rectangle screen)
    {
        InitializeComponent();

        // 覆盖整个虚拟桌面（DIP）
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        FullRect.Rect = new Rect(0, 0, Math.Max(1, Width), Math.Max(1, Height));

        _screen = screen.Width > 0 && screen.Height > 0
            ? screen
            : new System.Drawing.Rectangle(
                0, 0, (int)Math.Max(1, Math.Round(Width)), (int)Math.Max(1, Math.Round(Height)));

        // 冻结图铺满遮罩：框选时看到的就是即将被翻译的画面（屏幕上后来的变化不影响它）
        ShotImage.Source = ToDisplaySource(frozen);

        SelBorder.HorizontalAlignment = HorizontalAlignment.Left;
        SelBorder.VerticalAlignment = VerticalAlignment.Top;
        SizeTag.HorizontalAlignment = HorizontalAlignment.Left;
        SizeTag.VerticalAlignment = VerticalAlignment.Top;

        Loaded += (_, _) => { Activate(); Focus(); };
    }

    /// <summary>
    /// 唤醒遮罩并等待用户框选：先整屏冻结，再从冻结图裁剪选区。
    /// 返回裁剪结果（<see cref="CaptureResult.SelectionRect"/> 为物理像素区域）；取消返回 null。
    /// 若冻结失败，返回结果的 ImageBytes 为 null，调用方可用 SelectionRect 补一次实时截屏。
    /// </summary>
    public static CaptureResult? PickRegion()
    {
        // 关键顺序：遮罩弹出「之前」就截屏冻结。反过来（选完再截屏）会截到已经变化的画面，
        // 而且必须等遮罩消失后延时补拍，容易和屏幕上正在动的东西对不上。
        using var frozen = SelectionCapture.CaptureVirtualScreen(out var screen);
        if (frozen == null) Log.Info("截图翻译: 整屏冻结失败，改为选完后再截屏");

        var w = new ScreenCaptureWindow(frozen, screen);
        w.ShowDialog();

        if (w.Result is not { } region) return null;

        var png = frozen == null ? null : SelectionCapture.CropToPng(frozen, screen, region);
        return new CaptureResult
        {
            Kind = png != null ? CaptureKind.Image : CaptureKind.None,
            ImageBytes = png,
            SelectionRect = region,
        };
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
    private System.Drawing.Rectangle ToPhysical(Rect r)
    {
        // 窗口正好覆盖整个虚拟桌面，冻结图与窗口一一对应，直接按比例换算
        double kx = _screen.Width / Math.Max(1.0, Width);
        double ky = _screen.Height / Math.Max(1.0, Height);

        int x = _screen.X + (int)Math.Round(r.X * kx);
        int y = _screen.Y + (int)Math.Round(r.Y * ky);
        int w = (int)Math.Round(r.Width * kx);
        int h = (int)Math.Round(r.Height * ky);

        // 夹紧到虚拟桌面范围
        x = Math.Max(x, _screen.X);
        y = Math.Max(y, _screen.Y);
        w = Math.Min(w, _screen.Right - x);
        h = Math.Min(h, _screen.Bottom - y);
        return new System.Drawing.Rectangle(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>把冻结的整屏位图转成可显示的位图源（直接拷像素，不做 PNG 编解码，唤醒更快）。</summary>
    private static BitmapSource? ToDisplaySource(System.Drawing.Bitmap? bmp)
    {
        if (bmp == null) return null;
        try
        {
            var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var src = BitmapSource.Create(
                    bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgra32, null,
                    data.Scan0, data.Stride * bmp.Height, data.Stride);
                src.Freeze();
                return src;
            }
            finally { bmp.UnlockBits(data); }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
    }
}
