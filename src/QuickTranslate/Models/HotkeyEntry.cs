using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace QuickTranslate.Models;

/// <summary>一条全局热键配置。</summary>
public sealed class HotkeyEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "翻译";
    /// <summary>Translate | GenerateReply | ScreenshotOcr | InputTranslate</summary>
    public string Action { get; set; } = "Translate";
    /// <summary>目标语言（翻译 / 输入翻译动作有效）</summary>
    public string TargetLanguage { get; set; } = "中文";
    public bool Enabled { get; set; } = true;
    /// <summary>修饰键位掩码：1=Alt 2=Ctrl 4=Shift 8=Win</summary>
    public int Modifiers { get; set; }
    /// <summary>虚拟键码（VK）</summary>
    public int KeyVk { get; set; }
    /// <summary>该热键专用的系统提示词（留空则使用对应功能的默认提示词）</summary>
    public string Prompt { get; set; } = "";

    /// <summary>
    /// 该热键专用的模型（留空则使用「默认模型」）。
    /// 优先级：热键模型 &gt; 默认模型。
    /// </summary>
    public ModelRef Model { get; set; } = new();

    [JsonIgnore]
    public bool HasPrompt => !string.IsNullOrWhiteSpace(Prompt);

    [JsonIgnore]
    public bool HasModel => Model?.IsSet == true;

    [JsonIgnore]
    public string Display
    {
        get
        {
            if (KeyVk == 0) return "未设置";
            return $"{ModifierText}+{(Keys)KeyVk}";
        }
    }

    [JsonIgnore]
    public string ActionDisplay => Action switch
    {
        "GenerateReply" => "生成回复",
        "ScreenshotOcr" => "截图翻译",
        "InputTranslate" => "输入翻译",
        _ => "翻译",
    };

    private string ModifierText
    {
        get
        {
            var parts = new List<string>();
            if ((Modifiers & 8) != 0) parts.Add("Win");
            if ((Modifiers & 2) != 0) parts.Add("Ctrl");
            if ((Modifiers & 4) != 0) parts.Add("Shift");
            if ((Modifiers & 1) != 0) parts.Add("Alt");
            return string.Join("+", parts);
        }
    }
}
