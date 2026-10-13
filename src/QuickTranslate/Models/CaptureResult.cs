namespace QuickTranslate.Models;

public enum CaptureKind { None, Text, Image }

/// <summary>一次选中的内容捕获结果。</summary>
public sealed class CaptureResult
{
    public CaptureKind Kind { get; set; }
    public string Text { get; set; } = "";
    /// <summary>图片字节（统一为 PNG）</summary>
    public byte[]? ImageBytes { get; set; }
    /// <summary>选中内容在屏幕上的区域（物理像素），可能为空</summary>
    public System.Drawing.Rectangle SelectionRect { get; set; }

    public bool HasContent => Kind != CaptureKind.None;
}
