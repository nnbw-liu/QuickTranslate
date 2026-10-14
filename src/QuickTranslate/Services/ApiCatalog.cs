using QuickTranslate.Models;

namespace QuickTranslate.Services;

/// <summary>
/// 一次请求最终选定的「API + 模型」。
/// 除了模型名，还带着这个 API 自己的参数（最大输出 Token、图片翻译方式等）。
/// </summary>
public sealed class ModelSelection
{
    public required ApiProvider Provider { get; init; }
    public required string Model { get; init; }
    /// <summary>走哪一级选出来的：热键 / 功能 / 默认 / 兜底。</summary>
    public string Source { get; init; } = "";

    public string Display => $"{Provider.Name} / {Model}";

    /// <summary>图片翻译是否优先发原图给视觉模型（false = 先本地 OCR）。</summary>
    public bool VisionFirst => !string.Equals(Provider.ImageMode, "Ocr", StringComparison.OrdinalIgnoreCase);

    public int MaxTokens => Provider.MaxTokens;

    public ApiSettings ToApiSettings() => Provider.ToApiSettings(Model);

    /// <summary>日志用：模型 + 参数，一眼看出这次请求走的是哪个 API。</summary>
    public string Describe()
        => $"{Display}（来源={Source} 地址={Provider.BaseUrl} 温度={Provider.Temperature} " +
           $"最大输出={Provider.MaxTokens} 超时={Provider.TimeoutSeconds}s 图片={Provider.ImageMode}）";
}

/// <summary>
/// 模型编排：把「热键 → 功能 → 默认模型」解析成具体的 API + 模型，
/// 并按「API + 模型 + 参数」缓存 <see cref="OpenAIClient"/>（避免每次请求都新建 HttpClient）。
/// 设置变更时调用 <see cref="Clear"/> 让缓存失效。
/// </summary>
public sealed class ApiCatalog
{
    private readonly object _lock = new();
    private readonly Dictionary<string, OpenAIClient> _clients = new(StringComparer.Ordinal);
    private readonly List<string> _fallbackWarnings = new();

    /// <summary>最近一次解析遇到的问题（例如引用的 API 已被删除），供界面提示。</summary>
    public IReadOnlyList<string> Warnings
    {
        get { lock (_lock) return _fallbackWarnings.ToArray(); }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var c in _clients.Values) { try { c.Dispose(); } catch { } }
            _clients.Clear();
            _fallbackWarnings.Clear();
        }
    }

    // ---------- 解析 ----------

    /// <summary>
    /// 解析本次请求用哪个 API 的哪个模型。优先级：热键模型 &gt; 默认模型。
    /// 引用失效（API 被删/模型名为空）时**逐级回退**并在日志里写明，绝不静默用错模型。
    /// </summary>
    public ModelSelection Resolve(HotkeyEntry? entry = null)
    {
        var settings = App.Settings;

        // ① 热键级
        if (entry?.Model is { } hot && hot.IsSet)
        {
            var hit = TryProvider(settings, hot.ProviderId);
            if (hit != null) return new ModelSelection { Provider = hit, Model = hot.Model.Trim(), Source = $"热键“{entry.Name}”" };
            Warn($"热键“{entry.Name}”指定的 API 已不存在（id={hot.ProviderId}），改用默认模型。");
        }

        // ② 默认模型
        var def = settings.DefaultModel;
        if (def.IsSet)
        {
            var hit = TryProvider(settings, def.ProviderId);
            if (hit != null) return new ModelSelection { Provider = hit, Model = def.Model.Trim(), Source = "默认模型" };
            Warn($"默认模型指向的 API 已不存在（id={def.ProviderId}），尝试使用第一个可用的 API。");
        }

        // ④ 兜底：第一个配好模型的 API
        var first = settings.Providers.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.DefaultModel) || p.Models.Count > 0)
                    ?? settings.Providers.FirstOrDefault();
        if (first != null)
        {
            var model = !string.IsNullOrWhiteSpace(first.DefaultModel)
                ? first.DefaultModel.Trim()
                : first.Models.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))?.Trim() ?? "";
            if (model.Length > 0)
            {
                Warn("还没有设置默认模型，已临时使用第一个 API 的模型。请到 首选项 → 模型 里确认。");
                return new ModelSelection { Provider = first, Model = model, Source = "兜底（第一个 API）" };
            }
        }

        throw new ApiException("还没有可用的模型：请打开 首选项 → 模型，添加一个 API 并选择默认模型。");
    }

    private static ApiProvider? TryProvider(AppSettings s, string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : s.Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    private void Warn(string message)
    {
        Log.Info("模型解析: " + message);
        lock (_lock)
        {
            if (!_fallbackWarnings.Contains(message)) _fallbackWarnings.Add(message);
        }
    }

    // ---------- 客户端 ----------

    /// <summary>取（并缓存）某个「API + 模型」的客户端。参数变化会自动换新实例。</summary>
    public OpenAIClient ClientFor(ModelSelection sel)
    {
        var p = sel.Provider;
        var key = string.Join("|",
            p.Id, sel.Model,
            p.BaseUrl.Trim().TrimEnd('/').ToLowerInvariant(),
            p.ApiKey.Length, p.ApiKey.GetHashCode().ToString("X"),
            p.Temperature.ToString("0.###"), p.MaxTokens, p.TimeoutSeconds);
        lock (_lock)
        {
            if (_clients.TryGetValue(key, out var cached)) return cached;
            // 同一个 API 改了参数：把旧实例清掉，避免缓存无限增长
            foreach (var stale in _clients.Keys.Where(k => k.StartsWith(p.Id + "|", StringComparison.Ordinal)).ToList())
            {
                try { _clients[stale].Dispose(); } catch { }
                _clients.Remove(stale);
            }
            var client = new OpenAIClient(p.ToApiSettings(sel.Model))
            {
                SkipReasoningForTranslation = App.Settings.General.SkipReasoningForTranslation,
            };
            _clients[key] = client;
            return client;
        }
    }

    /// <summary>用当前设置里最新的「关闭思考」开关刷新已缓存的客户端。</summary>
    public void ApplyGeneralFlags()
    {
        lock (_lock)
        {
            foreach (var c in _clients.Values)
                c.SkipReasoningForTranslation = App.Settings.General.SkipReasoningForTranslation;
        }
    }

    // ---------- 界面辅助 ----------

    public IEnumerable<ApiProvider> Providers => App.Settings.Providers;

    public ApiProvider? ById(string? id) => TryProvider(App.Settings, id);

    /// <summary>把「API + 模型」显示成一句人话（用于热键列表、模型选择对话框）。</summary>
    public string Describe(ModelRef? model)
    {
        if (model?.IsSet != true) return "";
        var p = TryProvider(App.Settings, model.ProviderId);
        return p == null ? $"(已失效) {model.Model}" : $"{p.Name} / {model.Model}";
    }
}
