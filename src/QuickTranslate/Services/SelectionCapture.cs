using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

/// <summary>
/// 捕获系统中选中的文本或图片。
/// 策略：优先 UI Automation（不干扰剪贴板，还能给出选中区域），
/// 失败则使用“模拟 Ctrl+C → 读剪贴板 → 恢复剪贴板”的通用方案。
/// </summary>
public static class SelectionCapture
{
    public static CaptureResult Capture()
    {
        var fg = Native.NativeWin.GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            Native.NativeWin.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == Environment.ProcessId)
                Log.Info("capture: 前台窗口是本程序自身（弹窗/设置），UIA 与 Ctrl+C 将无法取得外部选中内容");
        }

        // ① 真正的“选中内容”：UI Automation 的 TextPattern（不碰剪贴板、能拿到选中区域）
        var viaUia = RunSta(TryGetTextViaUIA);
        if (viaUia.Kind == CaptureKind.Text)
        {
            Log.Info($"capture: UIA 选中命中，文本长度={viaUia.Text.Length}");
            return viaUia;
        }

        // ② 模拟 Ctrl+C：只有剪贴板序号真的变化，才说明“复制到了选中内容”，
        //    否则剪贴板里是上一次的旧内容，绝不能当成选中内容翻译（旧版本就是这么翻错的）。
        var viaClip = RunSta(CaptureViaClipboard);
        if (viaClip.HasContent) return viaClip;

        // ③ 兜底：焦点在单行输入框且确实没有选中时，取整个输入框内容（仅限 Edit/ComboBox，且长度有限）
        var viaValue = RunSta(TryGetValueFallback);
        if (viaValue.Kind == CaptureKind.Text)
        {
            Log.Info($"capture: 未检测到选中，回退取整个输入框内容，文本长度={viaValue.Text.Length}");
            return viaValue;
        }

        Log.Info("capture: 未获取到任何内容");
        return new CaptureResult();
    }

    /// <summary>
    /// 在遮罩弹出「之前」就把整个虚拟桌面冻结成一张位图（物理像素）。
    /// 框选时只从这张冻结图里裁剪，因此屏幕上后来发生的变化（动画/视频/窗口切换）
    /// 不会让“看到的内容”和“实际翻译的内容”对不上。
    /// bounds 始终返回虚拟桌面范围（即使截图失败），供坐标换算使用；失败返回 null。
    /// </summary>
    public static Bitmap? CaptureVirtualScreen(out Rectangle bounds)
    {
        bounds = Native.NativeWin.GetVirtualScreenBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0) bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        try
        {
            var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
    }

    /// <summary>从整屏冻结图里裁出屏幕区域（物理像素），返回 PNG 字节；失败返回 null。</summary>
    public static byte[]? CropToPng(Bitmap screen, Rectangle screenBounds, Rectangle region)
    {
        var r = Rectangle.Intersect(region, screenBounds);
        if (r.Width < 2 || r.Height < 2) return null;

        try
        {
            using var crop = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(crop))
                g.DrawImage(screen,
                    new Rectangle(0, 0, r.Width, r.Height),
                    new Rectangle(r.X - screenBounds.X, r.Y - screenBounds.Y, r.Width, r.Height),
                    GraphicsUnit.Pixel);
            using var ms = new MemoryStream();
            crop.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
    }

    /// <summary>截取屏幕上指定区域（物理像素），返回图片结果。仅作为冻结失败时的兜底。</summary>
    public static CaptureResult CaptureRegion(Rectangle rect)
    {
        if (rect.Width < 2 || rect.Height < 2) return new CaptureResult();
        var png = Native.NativeCore.CaptureScreenToPng(rect.X, rect.Y, rect.Width, rect.Height);
        if (png == null) return new CaptureResult();
        return new CaptureResult
        {
            Kind = CaptureKind.Image,
            ImageBytes = png,
            SelectionRect = rect,
        };
    }

    private static T RunSta<T>(Func<T> f)
    {
        T result = default!;
        var t = new Thread(() => result = f());
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    // ---------- UI Automation 方案 ----------

    private static CaptureResult TryGetTextViaUIA()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            var el = focused;
            for (int i = 0; el != null && i < 8; i++)
            {
                if (TryReadSelection(el, out var text, out var rect) && text.Length > 0)
                    return new CaptureResult { Kind = CaptureKind.Text, Text = text, SelectionRect = rect };
                el = TreeWalker.ControlViewWalker.GetParent(el);
            }

            var fg = Native.NativeWin.GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                var root = AutomationElement.FromHandle(fg);
                if (root != null)
                {
                    var docs = root.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                    foreach (AutomationElement d in docs)
                    {
                        if (TryReadSelection(d, out var text, out var rect) && text.Length > 0)
                            return new CaptureResult { Kind = CaptureKind.Text, Text = text, SelectionRect = rect };
                    }
                }
            }
        }
        catch { }
        return new CaptureResult();
    }

    /// <summary>只认“真正的选中内容”（TextPattern 的 selection）；不在这里读整个控件的 Value。</summary>
    private static bool TryReadSelection(AutomationElement el, out string text, out Rectangle rect)
    {
        text = ""; rect = default;
        try
        {
            if (el.TryGetCurrentPattern(TextPattern.Pattern, out var op) && op is TextPattern tp)
            {
                var sel = tp.GetSelection();
                if (sel != null && sel.Length > 0)
                {
                    var sb = new StringBuilder();
                    foreach (var r in sel) sb.Append(r.GetText(-1));
                    text = sb.ToString();
                    // TextPatternRange 无 GetBoundingRectangle，使用元素自身矩形作为定位参考
                    rect = ToRect(el.Current.BoundingRectangle);
                    return text.Length > 0;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 兜底：确实没有选中内容时，取整个输入框/下拉框的文本（仅 Edit/ComboBox，且长度受限，
    /// 避免在文档类控件上把整篇文档当成“选中内容”翻译）。
    /// </summary>
    private static CaptureResult TryGetValueFallback()
    {
        const int MaxChars = 1200;
        try
        {
            var el = AutomationElement.FocusedElement;
            for (int i = 0; el != null && i < 4; i++)
            {
                var ct = el.Current.ControlType;
                if (ct == ControlType.Edit || ct == ControlType.ComboBox)
                {
                    if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var ovp) && ovp is ValuePattern vp)
                    {
                        var v = (vp.Current.Value ?? "").Trim();
                        if (v.Length > 0 && v.Length <= MaxChars)
                            return new CaptureResult { Kind = CaptureKind.Text, Text = v, SelectionRect = ToRect(el.Current.BoundingRectangle) };
                    }
                    return new CaptureResult();
                }
                el = TreeWalker.ControlViewWalker.GetParent(el);
            }
        }
        catch { }
        return new CaptureResult();
    }

    private static Rectangle ToRect(System.Windows.Rect r)
        => r.IsEmpty ? default : new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);

    // ---------- 剪贴板方案 ----------

    private static CaptureResult CaptureViaClipboard()
    {
        try
        {
            IDataObject? saved = null;
            try { saved = Clipboard.GetDataObject(); } catch { }
            uint seq0 = Native.NativeWin.GetClipboardSequenceNumber();

            if (!Native.NativeCore.CopySelection())
            {
                Log.Info("capture: 模拟 Ctrl+C 失败");
                return new CaptureResult();
            }

            // 等待剪贴板变化（最多约 1.2s）
            bool changed = false;
            for (int i = 0; i < 48; i++)
            {
                Thread.Sleep(25);
                if (Native.NativeWin.GetClipboardSequenceNumber() != seq0) { changed = true; break; }
            }
            Log.Info($"capture: 剪贴板方案 seq0={seq0} 复制后序列变化={changed}");

            if (!changed)
            {
                // Ctrl+C 没复制到任何东西（没有选中内容 / 该程序不支持复制）：
                // 此时剪贴板里是旧内容，绝不能再拿来当“选中内容”翻译。
                Log.Info("capture: Ctrl+C 未复制到新内容，剪贴板里是旧内容，按未选中处理");
                return new CaptureResult();
            }

            string text = "";
            Image? img = null;
            try
            {
                if (Clipboard.ContainsText()) text = Clipboard.GetText();
                img = Clipboard.GetImage();
            }
            catch { }
            Log.Info($"capture: 剪贴板读取 文本长度={text.Length} 有图片={img != null}");

            // 恢复原剪贴板
            if (saved != null)
            {
                try { Clipboard.SetDataObject(saved, true); } catch { }
            }

            if (text.Length > 0)
                return new CaptureResult { Kind = CaptureKind.Text, Text = text };

            if (img != null)
            {
                using (img)
                using (var ms = new MemoryStream())
                {
                    img.Save(ms, ImageFormat.Png);
                    return new CaptureResult { Kind = CaptureKind.Image, ImageBytes = ms.ToArray() };
                }
            }
        }
        catch { }
        return new CaptureResult();
    }
}
