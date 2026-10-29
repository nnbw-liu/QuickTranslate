using System.Text.Json.Serialization;

namespace SDemo.QuickTranslate.Models;

/// <summary>应用设置（持久化为 JSON）。</summary>
public sealed class AppSettings
{
    /// <summary>
    /// 多个 API（供应商）。每个 API 自带接口地址 / Key / 模型列表 / 温度 / 最大输出 Token / 超时 / 图片翻译方式，
    /// 这样「便宜的快模型」和「能看图的视觉模型」可以各配一个 API，按功能或按热键选用。
    /// </summary>
    public List<ApiProvider> Providers { get; set; } = new();

    /// <summary>默认模型（供应商 + 模型）：没单独指定模型的地方都用它。</summary>
    public ModelRef DefaultModel { get; set; } = new();

    /// <summary>【旧字段】单 API 时代的一份配置，仅用于兼容旧配置与旧版本；加载时迁移到 <see cref="Providers"/>。</summary>
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
        InputTranslateHotkey(),
    };

    /// <summary>「输入翻译」热键（默认 Ctrl+Alt+I）：按下弹输入框，填入内容后翻译。</summary>
    public static HotkeyEntry InputTranslateHotkey() => new()
    {
        Name = "输入翻译",
        Action = "InputTranslate",
        TargetLanguage = "",   // 留空 = 用上次用过的目标语言
        Modifiers = 3,         // Ctrl + Alt
        KeyVk = 73,            // I
    };
}

/// <summary>
/// 一个 OpenAI 兼容 API（供应商）：地址、Key、可用模型与请求参数。
/// 参数也可由 <see cref="ApiSettings"/> 承载（调用 <see cref="ToApiSettings"/> 转换后交给 <see cref="Services.OpenAIClient"/>）。
/// </summary>
public sealed class ApiProvider
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名（如 OpenAI / DeepSeek / 本地 LM Studio），仅用于界面与日志。</summary>
    public string Name { get; set; } = "API";

    /// <summary>接口类型（预留）：目前只实现 OpenAI 兼容的 chat/completions。</summary>
    public string Api { get; set; } = "openai-completions";

    /// <summary>OpenAI 兼容接口地址，例如 https://api.openai.com/v1</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";

    /// <summary>这个 API 下的可用模型（「获取模型列表」拉取后缓存，也可手动维护）。</summary>
    public List<string> Models { get; set; } = new();

    /// <summary>这个 API 的常用模型：用于「测试连接」，界面里也会预选它（可为空）。</summary>
    public string DefaultModel { get; set; } = "";

    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 2048;
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>图片翻译方式：Vision=发送原图给视觉模型；Ocr=本地 OCR 后翻译</summary>
    public string ImageMode { get; set; } = "Vision";

    /// <summary>是否为视觉模型（仅供界面提示，不参与判断；图片翻译时失败仍会自动回退本地 OCR）。</summary>
    public bool Vision { get; set; }

    /// <summary>转成一次请求用的参数（把模型名填进去）。</summary>
    public ApiSettings ToApiSettings(string model) => new()
    {
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        Model = model,
        KnownModels = Models,
        Temperature = Temperature,
        MaxTokens = MaxTokens,
        TimeoutSeconds = TimeoutSeconds,
        ImageMode = ImageMode,
    };

    /// <summary>把旧版单 API 配置升级成一个供应商。</summary>
    public static ApiProvider FromLegacy(ApiSettings a)
    {
        var p = new ApiProvider
        {
            Name = GuessName(a.BaseUrl),
            BaseUrl = string.IsNullOrWhiteSpace(a.BaseUrl) ? "https://api.openai.com/v1" : a.BaseUrl.Trim(),
            ApiKey = a.ApiKey ?? "",
            Temperature = a.Temperature,
            MaxTokens = a.MaxTokens,
            TimeoutSeconds = a.TimeoutSeconds,
            ImageMode = string.IsNullOrWhiteSpace(a.ImageMode) ? "Vision" : a.ImageMode,
            DefaultModel = a.Model ?? "",
        };
        if (a.KnownModels is { Count: > 0 }) p.Models = a.KnownModels.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToList();
        if (!string.IsNullOrWhiteSpace(a.Model) && !p.Models.Contains(a.Model)) p.Models.Insert(0, a.Model);
        return p;
    }

    /// <summary>用接口地址猜一个好认的名字（旧配置迁移时用）。</summary>
    public static string GuessName(string? baseUrl)
    {
        try
        {
            if (Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out var u))
                return u.IsLoopback ? $"本地 {u.Host}:{u.Port}" : u.Host;
        }
        catch { }
        return "默认 API";
    }

    /// <summary>模型候选：Models + DefaultModel，去重并去掉空项。</summary>
    public List<string> ModelCandidates()
    {
        var list = new List<string>();
        void Add(string? m)
        {
            m = (m ?? "").Trim();
            if (m.Length > 0 && !list.Contains(m, StringComparer.OrdinalIgnoreCase)) list.Add(m);
        }
        Add(DefaultModel);
        foreach (var m in Models) Add(m);
        return list;
    }

    /// <summary>是否本机接口（本机接口通常不需要 API Key，不必提醒）。</summary>
    public bool IsLocalAddress
    {
        get
        {
            try
            {
                if (Uri.TryCreate((BaseUrl ?? "").Trim(), UriKind.Absolute, out var u))
                    return u.IsLoopback ||
                           u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                           u.Host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            return false;
        }
    }
}

/// <summary>一处「用哪个 API 的哪个模型」的引用；任一项为空表示「跟随上一级」。</summary>
public sealed class ModelRef
{
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";

    [JsonIgnore]
    public bool IsSet => !string.IsNullOrWhiteSpace(ProviderId) && !string.IsNullOrWhiteSpace(Model);

    public ModelRef Clone() => new() { ProviderId = ProviderId, Model = Model };

    public void Clear() { ProviderId = ""; Model = ""; }

    public bool SameAs(ModelRef? other)
        => other != null
           && string.Equals(ProviderId, other.ProviderId, StringComparison.Ordinal)
           && string.Equals(Model, other.Model, StringComparison.Ordinal);
}

/// <summary>
/// 一次请求的参数（地址 / Key / 模型 / 温度 / 最大输出 Token / 超时 / 图片翻译方式）。
/// 由 <see cref="ApiProvider.ToApiSettings"/> 生成交给 <see cref="Services.OpenAIClient"/>；
/// 同时作为旧版单 API 配置的兼容载体（见 <see cref="AppSettings.Api"/>）。
/// </summary>
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
    /// <summary>
    /// 翻译类请求关闭思考（直出译文，更快更省 token）。默认开启；生成回复仍保留思考过程。
    /// 关闭方式（不同服务端认的参数不同）见 <see cref="Services.OpenAIClient"/> 的 ThinkOffProfiles。
    /// </summary>
    public bool SkipReasoningForTranslation { get; set; } = true;

    /// <summary>「输入翻译」窗口上次使用的目标语言（下次打开时预选）。</summary>
    public string LastInputTargetLanguage { get; set; } = "中文";

    /// <summary>是否已经为老配置自动补过「输入翻译」热键（补过一次就不再补，用户删掉后不会自己回来）。</summary>
    public bool InputTranslateHotkeyAdded { get; set; }

    /// <summary>把旧配置里的全局提示词迁移到「翻译提示词」。</summary>
    public void MigrateLegacy()
    {
        if (string.IsNullOrWhiteSpace(TranslatePrompt) && !string.IsNullOrWhiteSpace(CustomSystemPrompt))
            TranslatePrompt = CustomSystemPrompt;
    }
}
