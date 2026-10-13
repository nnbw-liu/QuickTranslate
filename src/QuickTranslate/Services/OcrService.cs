using System.IO;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace QuickTranslate.Services;

/// <summary>本地图片文字识别。优先使用 C++ 原生 OCR，失败回退到托管 WinRT OCR。</summary>
public static class OcrService
{
    public static async Task<string?> RecognizeAsync(byte[] pngBytes)
    {
        if (pngBytes == null || pngBytes.Length == 0) return null;

        var tmp = Path.Combine(Path.GetTempPath(), $"qt_ocr_{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(tmp, pngBytes);

            var native = await Task.Run(() => Native.NativeCore.OcrFile(tmp));
            if (native != null) return native;

            return await ManagedOcrAsync(tmp);
        }
        catch { return null; }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static async Task<string?> ManagedOcrAsync(string path)
    {
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) return null;

            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
            var result = await engine.RecognizeAsync(softwareBitmap);

            var sb = new StringBuilder();
            foreach (var line in result.Lines) sb.AppendLine(line.Text);
            var t = sb.ToString().Trim();
            return t.Length > 0 ? t : null;
        }
        catch { return null; }
    }
}
