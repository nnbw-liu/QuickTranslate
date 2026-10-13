namespace QuickTranslate.Models;

/// <summary>应用设置（持久化为 JSON）。</summary>
public sealed class AppSettings
{
    public ApiSettings Api { get; set; } = new();
    public List<HotkeyEntry> Hotkeys { get; set; } = DefaultHotkeys();
    public GeneralSettings General { get; set; } = new();

    public static List<HotkeyEntry> DefaultHotkeys() => new()
    {
        new HotkeyEntry { Name = "翻译为中文", Action = "Translate", TargetLanguage = "中文", Modifiers = 3, KeyVk = 84 },
        new HotkeyEntry { Name = "翻译为英文", Action = "Translate", TargetLanguage = "English", Modifiers = 3, KeyVk = 69 },
        new HotkeyEntry { Name = "翻译为日语", Action = "Translate", TargetLanguage = "日本語", Modifiers = 3, KeyVk = 74 },
        new HotkeyEntry { Name = "生成回复", Action = "GenerateReply", TargetLanguage = "", Modifiers = 3, KeyVk = 82 },
        new HotkeyEntry { Name = "截图翻译", Action = "ScreenshotOcr", TargetLanguage = "中文", Modifiers = 3, KeyVk = 81 },
    };
}

public sealed class ApiSettings
{
    /// <summary>OpenAI 兼容接口地址，例如 https://api.openai.com/v1</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    /// <summary>从接口「获取模型列表」后缓存的模型名，用于下次打开首选项时直接下拉选择。</summary>
    public List<string> KnownModels { get; set; } = new();
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 2048;
    public int TimeoutSeconds { get; set; } = 60;
    /// <summary>图片翻译方式：Vision=发送原图给视觉模型；Ocr=本地 OCR 后翻译</summary>
    public string ImageMode { get; set; } = "Vision";
}

public sealed class GeneralSettings
{
    public bool AutoStart { get; set; }
    /// <summary>Above=优先显示在选中内容上方；Auto=自动</summary>
    public string PopupPosition { get; set; } = "Above";
    /// <summary>【旧字段】全局自定义系统提示词，仅用于兼容旧配置，加载时迁移到 <see cref="TranslatePrompt"/></summary>
    public string CustomSystemPrompt { get; set; } = "";
    /// <summary>「翻译」功能的默认系统提示词（留空用内置）</summary>
    public string TranslatePrompt { get; set; } = "";
    /// <summary>「生成回复」功能的默认系统提示词（留空用内置）</summary>
    public string ReplyPrompt { get; set; } = "";
    /// <summary>「截图翻译」功能的默认系统提示词（留空用内置）</summary>
    public string ScreenshotPrompt { get; set; } = "";
    /// <summary>翻译时跳过思考过程（直出翻译，更快）。默认开启；生成回复仍保留思考过程流式输出。</summary>
    public bool SkipReasoningForTranslation { get; set; } = true;

    /// <summary>把旧配置里的全局提示词迁移到「翻译提示词」。</summary>
    public void MigrateLegacy()
    {
        if (string.IsNullOrWhiteSpace(TranslatePrompt) && !string.IsNullOrWhiteSpace(CustomSystemPrompt))
            TranslatePrompt = CustomSystemPrompt;
    }
}
