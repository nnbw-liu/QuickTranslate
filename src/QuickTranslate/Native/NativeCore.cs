using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickTranslate.Native;

/// <summary>
/// C++ 原生库 NativeCore.dll 的 P/Invoke 封装。
/// 原生库负责系统级操作（模拟复制、屏幕截图、OCR）；
/// 原生库缺失或调用失败时自动回退到托管实现，保证应用始终可用。
/// </summary>
public static class NativeCore
{
    private const string DllName = "NativeCore.dll";

    public static readonly bool Available;
    public static readonly string? LoadError;

    static NativeCore()
    {
        var path = Path.Combine(AppContext.BaseDirectory, DllName);
        if (File.Exists(path)) { Available = true; }
        else { Available = false; LoadError = "NativeCore.dll 不存在，已启用托管回退。"; }
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int QtCopySelection();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int QtCaptureScreenToBmp(int x, int y, int w, int h, string outBmpPath);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int QtOcrFromImageFile(string imagePath, StringBuilder outBuf, int outCap);

    /// <summary>模拟 Ctrl+C 复制选中内容。返回是否成功。</summary>
    public static bool CopySelection()
    {
        if (Available)
        {
            try { if (QtCopySelection() == 0) return true; } catch { }
        }
        try { NativeWin.SendCopy(); return true; } catch { return false; }
    }

    /// <summary>截取屏幕区域，返回 PNG 字节；失败返回 null。</summary>
    public static byte[]? CaptureScreenToPng(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;

        if (Available)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"qt_cap_{Guid.NewGuid():N}.bmp");
            try
            {
                if (QtCaptureScreenToBmp(x, y, w, h, tmp) == 0 && File.Exists(tmp))
                {
                    using var bmp = new Bitmap(tmp);
                    using var ms = new MemoryStream();
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
            catch { }
            finally { TryDelete(tmp); }
        }

        // 托管回退：GDI CopyFromScreen
        try
        {
            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch { return null; }
    }

    /// <summary>原生 OCR（C++/WinRT Windows.Media.Ocr）。无原生库/失败返回 null。</summary>
    public static string? OcrFile(string imagePath)
    {
        if (!Available) return null;
        try
        {
            var sb = new StringBuilder(65536);
            int rc = QtOcrFromImageFile(imagePath, sb, sb.Capacity);
            return rc == 0 ? sb.ToString().Trim() : null;
        }
        catch { return null; }
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { }
    }
}
