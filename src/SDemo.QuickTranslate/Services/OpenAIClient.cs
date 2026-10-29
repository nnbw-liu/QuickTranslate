using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SDemo.QuickTranslate.Models;

namespace SDemo.QuickTranslate.Services;

public sealed class ApiException : Exception
{
    public int StatusCode { get; }
    public ApiException(string message, int statusCode = 0) : base(message) { StatusCode = statusCode; }
}

/// <summary>
/// 一次流式增量：思考内容(reasoning) 与 正文(content) 可分别出现。
/// <see cref="FinishReason"/> / <see cref="CompletionTokens"/> 是服务端附带的结束原因与用量
/// （只有部分接口会给，用于判断「是否被最大输出 Token 截断」）。
/// </summary>
public readonly record struct StreamDelta(string? Content, string? Reasoning, string? FinishReason = null, int? CompletionTokens = null);

/// <summary>一次非流式探测的结果。</summary>
internal readonly record struct ChatProbe(string? Content, string? Reasoning, string? FinishReason);

/// <summary>OpenAI 兼容 Chat Completions 客户端（支持流式输出与视觉模型输入）。</summary>
public sealed class OpenAIClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ApiSettings _cfg;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public OpenAIClient(ApiSettings cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(cfg.TimeoutSeconds, 5, 600)) };
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());
    }

    public string Model => _cfg.Model;

    /// <summary>
    /// 翻译时关闭思考（生成回复始终保留思考过程）。
    /// 关闭方式见 <see cref="ThinkOff"/>：不同服务端认的参数不同，会一次性带上并记住可用的那种。
    /// </summary>
    public bool SkipReasoningForTranslation { get; set; } = true;

    /// <summary>最近一次请求/探测实际生效的「关闭思考」说明，供「测试连接」显示。</summary>
    public string? LastThinkingNote { get; private set; }

    // ---------- 文本翻译（流式） ----------

    /// <param name="prompt">自定义系统提示词；留空使用内置翻译提示词。</param>
    public IAsyncEnumerable<StreamDelta> TranslateStreamAsync(string text, string lang, CancellationToken ct = default, string? prompt = null)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ApiException("没有可翻译的内容。");
        var skip = SkipReasoningForTranslation;
        var sys = !string.IsNullOrWhiteSpace(prompt)
            ? prompt.Trim()
            : $"You are a professional translator. Translate the user's text into {lang}. " +
              "Preserve the original formatting, line breaks, code blocks and Markdown. " +
              "Output ONLY the translation, with no explanations, no quotes, no notes.";
        if (skip) sys += NoReasoningSuffix;
        return CompleteStreamAsync(sys, text, null, ct, skip);
    }

    // ---------- 图片翻译（流式） ----------

    /// <param name="prompt">自定义系统提示词；留空使用内置图片翻译提示词。</param>
    public IAsyncEnumerable<StreamDelta> TranslateImageStreamAsync(byte[] png, string lang, CancellationToken ct = default, string? prompt = null)
    {
        if (png == null || png.Length == 0) throw new ApiException("没有可翻译的图片。");
        var skip = SkipReasoningForTranslation;
        var sys = !string.IsNullOrWhiteSpace(prompt)
            ? prompt.Trim()
            : $"You are a professional translator. The image contains text. " +
              $"Translate ALL readable text in the image into {lang}. " +
              "Preserve line breaks and layout as much as possible. " +
              "Output ONLY the translation, with no explanations.";
        if (skip) sys += NoReasoningSuffix;
        return CompleteStreamAsync(sys, "请翻译图片中的文字。", png, ct, skip);
    }

    // ---------- 生成回复（流式） ----------

    /// <param name="prompt">自定义系统提示词；留空使用内置回复提示词。</param>
    public IAsyncEnumerable<StreamDelta> GenerateReplyStreamAsync(string context, string? lang, CancellationToken ct = default, string? prompt = null)
    {
        var sys = !string.IsNullOrWhiteSpace(prompt)
            ? prompt.Trim()
            : string.IsNullOrWhiteSpace(lang)
                ? "You are a helpful assistant. Based on the user's selected message or context, " +
                  "write a natural, appropriate reply. Match the language of the input content. " +
                  "Output ONLY the reply text, no explanations."
                : $"You are a helpful assistant. Based on the user's selected message or context, " +
                  $"write a natural, appropriate reply in {lang}. " +
                  "Output ONLY the reply text, no explanations.";
        return CompleteStreamAsync(sys, context, null, ct);
    }

    /// <summary>基于图片内容生成回复（本地 OCR 失败时的兜底）。</summary>
    /// <param name="prompt">自定义系统提示词；留空使用内置回复提示词。</param>
    public IAsyncEnumerable<StreamDelta> GenerateReplyFromImageStreamAsync(byte[] png, string? lang, CancellationToken ct = default, string? prompt = null)
    {
        if (png == null || png.Length == 0) throw new ApiException("没有可用的图片。");
        var sys = !string.IsNullOrWhiteSpace(prompt)
            ? prompt.Trim()
            : string.IsNullOrWhiteSpace(lang)
                ? "You are a helpful assistant. Read the text in the image, " +
                  "then write a natural, appropriate reply based on it. Match the language of the image content. " +
                  "Output ONLY the reply text, no explanations."
                : $"You are a helpful assistant. Read the text in the image, " +
                  $"then write a natural, appropriate reply based on it in {lang}. " +
                  "Output ONLY the reply text, no explanations.";
        return CompleteStreamAsync(sys, "请阅读图片中的内容并撰写合适的回复。", png, ct);
    }

    private const string NoReasoningSuffix =
        " Do NOT provide any step-by-step reasoning or thinking process. Output the translation directly.";

    // ---------- 非流式封装（其余流程统一走流式） ----------

    public async Task<string> TranslateAsync(string text, string lang, CancellationToken ct = default, string? prompt = null)
    {
        var sb = new StringBuilder();
        await foreach (var d in TranslateStreamAsync(text, lang, ct, prompt)) sb.Append(d.Content);
        return Finish(sb.ToString());
    }

    public async Task<string> TranslateImageAsync(byte[] png, string lang, CancellationToken ct = default, string? prompt = null)
    {
        var sb = new StringBuilder();
        await foreach (var d in TranslateImageStreamAsync(png, lang, ct, prompt)) sb.Append(d.Content);
        return Finish(sb.ToString());
    }

    public async Task<string> GenerateReplyAsync(string context, string? lang, CancellationToken ct = default, string? prompt = null)
    {
        var sb = new StringBuilder();
        await foreach (var d in GenerateReplyStreamAsync(context, lang, ct, prompt)) sb.Append(d.Content);
        return Finish(sb.ToString());
    }

    /// <summary>
    /// 连接测试：① GET /models 验证地址与 Key；② 一次最小对话请求验证模型真的能应答。
    /// 注意不能用过小的 max_tokens：思考型模型（如 deepseek 系列）会把 token 全用在
    /// reasoning_content 上，导致正文为空，看起来像“接口不可用”。
    /// </summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        int? modelCount = null;
        string? modelsNote = null;
        try { modelCount = (await ListModelsAsync(ct)).Count; }
        catch (Exception ex) { modelsNote = Trim(ex.Message, 90); }

        var probe = await ProbeChatAsync(ct);
        sw.Stop();

        var parts = new List<string> { $"连接成功（{sw.ElapsedMilliseconds} ms）" };
        parts.Add(modelCount.HasValue
            ? $"接口地址与 API Key 有效，共 {modelCount.Value} 个可用模型"
            : $"未能读取模型列表（{modelsNote}），不影响对话测试");
        if (!string.IsNullOrWhiteSpace(LastThinkingNote)) parts.Add(LastThinkingNote!);

        if (!string.IsNullOrWhiteSpace(probe.Content))
            parts.Add($"模型 {_cfg.Model} 正常应答：{Trim(probe.Content!, 60)}");
        else if (!string.IsNullOrWhiteSpace(probe.Reasoning))
            parts.Add($"模型 {_cfg.Model} 有响应，但回答全部都落在「思考过程」里（finish_reason={probe.FinishReason ?? "?"}）——" +
                      "这是思考型模型，正式翻译时请把「最大输出 Token」调大一些");
        else
            parts.Add($"模型 {_cfg.Model} 返回了空内容（finish_reason={probe.FinishReason ?? "?"}），请检查模型名与请求参数");

        return string.Join("；", parts);
    }

    // ---------- 关闭思考的参数组合 ----------

    /// <summary>
    /// 「关闭思考」的参数组合（不同服务端认的参数完全不同）：
    /// DeepSeek 用 <c>"thinking":{"type":"disabled"}</c>；通义千问(DashScope) 用 <c>"enable_thinking":false</c>；
    /// vLLM / llama.cpp 用 <c>"chat_template_kwargs":{"enable_thinking":false}</c>；
    /// LM Studio / OpenAI 风格用 <c>"reasoning_effort":"none"</c>。
    /// 服务端对不认识的参数一般直接忽略（HTTP 200），所以一次性全带上；
    /// 只有返回 400/422（参数不合法）的服务端才逐个去掉重试，成功的那种会被记住，后续请求不再试错。
    /// </summary>
    [Flags]
    private enum ThinkOff
    {
        None = 0,
        Thinking = 1,
        EnableThinking = 2,
        TemplateKwargs = 4,
        ReasoningEffort = 8,
    }

    /// <summary>从「全都带上」到「逐个放弃」，最后是「关不掉，照常思考」。</summary>
    private static readonly ThinkOff[] ThinkOffProfiles =
    {
        ThinkOff.Thinking | ThinkOff.EnableThinking | ThinkOff.TemplateKwargs | ThinkOff.ReasoningEffort,
        ThinkOff.EnableThinking | ThinkOff.TemplateKwargs | ThinkOff.ReasoningEffort,
        ThinkOff.EnableThinking | ThinkOff.ReasoningEffort,
        ThinkOff.ReasoningEffort,
        ThinkOff.Thinking,
        ThinkOff.None,
    };

    /// <summary>记住「地址|模型」上确认可用的关闭思考方式与 token 参数名，避免每次请求都试错。</summary>
    private static readonly ConcurrentDictionary<string, int> ThinkOffCache = new();
    private static readonly ConcurrentDictionary<string, byte> CompletionTokensCache = new();

    private string CacheKey() => $"{(_cfg.BaseUrl ?? "").Trim().TrimEnd('/').ToLowerInvariant()}|{_cfg.Model}";

    private static string Describe(ThinkOff style)
    {
        if (style == ThinkOff.None) return "未关闭（接口不接受任何关闭思考的参数）";
        var parts = new List<string>();
        if (style.HasFlag(ThinkOff.Thinking)) parts.Add("thinking.type=disabled");
        if (style.HasFlag(ThinkOff.EnableThinking)) parts.Add("enable_thinking=false");
        if (style.HasFlag(ThinkOff.TemplateKwargs)) parts.Add("chat_template_kwargs.enable_thinking=false");
        if (style.HasFlag(ThinkOff.ReasoningEffort)) parts.Add("reasoning_effort=none");
        return string.Join(" + ", parts);
    }

    private string BuildUrl(string path)
    {
        if (string.IsNullOrWhiteSpace(_cfg.BaseUrl))
            throw new ApiException("请先填写接口地址 (Base URL)。");
        return _cfg.BaseUrl.Trim().TrimEnd('/') + "/" + path;
    }

    /// <summary>最大输出 Token 参数值；≤0 表示不限制（不发送该参数）。</summary>
    private int? MaxTokensParam => _cfg.MaxTokens > 0 ? _cfg.MaxTokens : null;

    // ---------- 发送请求（带「关闭思考」参数组合的自动降级） ----------

    /// <summary>
    /// 发送流式对话请求：按 <see cref="ThinkOffProfiles"/> 依次尝试关闭思考的参数组合，
    /// 接口不认识（HTTP 400/422）就换下一种，全都拒绝时最后不带任何参数发送（此时模型仍会思考，思考内容会实时显示）。
    /// </summary>
    private async Task<HttpResponseMessage> SendStreamRequestAsync(
        string system, string user, byte[]? imagePng, CancellationToken ct, bool disableThinking)
    {
        var url = BuildUrl("chat/completions");
        var key = CacheKey();
        bool useCompletionTokens = CompletionTokensCache.ContainsKey(key);
        int profileCount = disableThinking ? ThinkOffProfiles.Length : 1;
        int start = disableThinking && ThinkOffCache.TryGetValue(key, out var cached) && cached < profileCount ? cached : 0;
        bool fromCache = start > 0;
        (int Code, string Body) last = (0, "");

        for (int i = start; i < profileCount; i++)
        {
            var style = disableThinking ? ThinkOffProfiles[i] : ThinkOff.None;
            using var request = BuildStreamRequest(url, system, user, imagePng, style, useCompletionTokens);
            var resp = await SendOnceAsync(request, ct);

            if (resp.IsSuccessStatusCode)
            {
                if (disableThinking)
                {
                    ThinkOffCache[key] = i;
                    Log.Info(style == ThinkOff.None
                        ? "接口不接受任何「关闭思考」的参数：本次请求仍会让模型思考（思考内容会实时显示在浮窗里）"
                        : $"已关闭思考：{Describe(style)}");
                }
                if (useCompletionTokens) CompletionTokensCache.TryAdd(key, 0);
                return resp;
            }

            int code = (int)resp.StatusCode;
            string body = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            last = (code, body);

            // OpenAI 新模型（o 系 / gpt-5）不再支持 max_tokens，必须换成 max_completion_tokens
            if (!useCompletionTokens && code is 400 or 422 &&
                body.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("接口要求用 max_completion_tokens 代替 max_tokens，已改用该参数重试");
                useCompletionTokens = true;
                i = start - 1; // 重新试一遍（for 会先 ++i）
                continue;
            }

            if (code is not (400 or 422))
                throw new ApiException(BuildError(code, body), code);

            Log.Info($"参数组合被接口拒绝（HTTP {code}，{Describe(style)}），尝试下一种：{Trim(body, 100)}");
            if (i == start && fromCache)
            {
                // 之前记住的组合失效了（换了模型/升级了服务端），清掉缓存从头再试
                ThinkOffCache.TryRemove(key, out _);
                fromCache = false;
                i = -1;
            }
        }

        throw new ApiException(BuildError(last.Code, last.Body), last.Code);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException($"请求超时（{_cfg.TimeoutSeconds} 秒）：接口没有及时响应。可在首选项里调大「超时」。");
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException($"无法连接接口：{ex.Message}（请检查「接口地址」与网络）");
        }
    }

    private HttpRequestMessage BuildStreamRequest(
        string url, string system, string user, byte[]? imagePng, ThinkOff style, bool completionTokens)
    {
        var req = new ChatRequest
        {
            model = _cfg.Model,
            temperature = _cfg.Temperature,
            max_tokens = completionTokens ? null : MaxTokensParam,
            max_completion_tokens = completionTokens ? MaxTokensParam : null,
            stream = true,
            thinking = style.HasFlag(ThinkOff.Thinking) ? new ThinkingOption() : null,
            enable_thinking = style.HasFlag(ThinkOff.EnableThinking) ? false : null,
            chat_template_kwargs = style.HasFlag(ThinkOff.TemplateKwargs) ? new ChatTemplateKwargs() : null,
            reasoning_effort = style.HasFlag(ThinkOff.ReasoningEffort) ? "none" : null,
            messages =
            {
                new ChatMessage { role = "system", content = system },
                BuildUserMessage(user, imagePng),
            },
        };

        var json = JsonSerializer.Serialize(req, JsonOpts);
        return new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Absolute))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>最小对话探测（非流式，同样会依次尝试关闭思考的参数组合）。</summary>
    private async Task<ChatProbe> ProbeChatAsync(CancellationToken ct)
    {
        var url = BuildUrl("chat/completions");
        var key = CacheKey();
        bool useCompletionTokens = CompletionTokensCache.ContainsKey(key);
        int start = ThinkOffCache.TryGetValue(key, out var cached) && cached < ThinkOffProfiles.Length ? cached : 0;
        bool fromCache = start > 0;
        (int Code, string Body) last = (0, "");

        for (int i = start; i < ThinkOffProfiles.Length; i++)
        {
            var style = ThinkOffProfiles[i];
            var (code, body) = await ProbeOnceAsync(url, ct, style, useCompletionTokens);
            last = (code, body);

            if (code is >= 200 and <= 299)
            {
                ThinkOffCache[key] = i;
                LastThinkingNote = style == ThinkOff.None
                    ? "该接口不接受「关闭思考」的参数，翻译时模型仍会思考（思考过程会实时显示）"
                    : $"翻译时会关闭思考（{Describe(style)}）";
                if (useCompletionTokens) CompletionTokensCache.TryAdd(key, 0);
                return ParseProbe(body);
            }

            if (!useCompletionTokens && code is 400 or 422 &&
                body.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("探测：接口要求用 max_completion_tokens，已改用该参数重试");
                useCompletionTokens = true;
                i = start - 1;
                continue;
            }

            if (code is not (400 or 422))
                throw new ApiException(BuildError(code, body), code);

            Log.Info($"探测：参数组合被接口拒绝（HTTP {code}，{Describe(style)}），尝试下一种");
            if (i == start && fromCache)
            {
                ThinkOffCache.TryRemove(key, out _);
                fromCache = false;
                i = -1;
            }
        }

        throw new ApiException(BuildError(last.Code, last.Body), last.Code);
    }

    private async Task<(int Code, string Body)> ProbeOnceAsync(string url, CancellationToken ct, ThinkOff style, bool completionTokens)
    {
        var req = new ChatRequest
        {
            model = _cfg.Model,
            temperature = _cfg.Temperature,
            max_tokens = completionTokens ? null : 64,
            max_completion_tokens = completionTokens ? 64 : null,
            thinking = style.HasFlag(ThinkOff.Thinking) ? new ThinkingOption() : null,
            enable_thinking = style.HasFlag(ThinkOff.EnableThinking) ? false : null,
            chat_template_kwargs = style.HasFlag(ThinkOff.TemplateKwargs) ? new ChatTemplateKwargs() : null,
            reasoning_effort = style.HasFlag(ThinkOff.ReasoningEffort) ? "none" : null,
            messages =
            {
                new ChatMessage
                {
                    role = "system",
                    content = "You are a helpful assistant. Do not explain, do not think step by step. Reply with exactly: OK",
                },
                new ChatMessage { role = "user", content = "ping" },
            },
        };

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(req, JsonOpts), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(new Uri(url, UriKind.Absolute), content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return ((int)resp.StatusCode, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException($"请求超时（{_cfg.TimeoutSeconds} 秒）：接口没有及时响应。可在首选项里调大「超时」。");
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException($"无法连接接口：{ex.Message}（请检查「接口地址」与网络）");
        }
    }

    /// <summary>解析非流式响应：正文、思考内容与结束原因（缺字段不抛异常）。</summary>
    internal static ChatProbe ParseProbe(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new ApiException("响应中没有 choices，可能不是 OpenAI 兼容接口。");

            var c0 = choices[0];
            var finish = Str(c0, "finish_reason");
            string? text = null, reasoning = null;
            if (c0.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
            {
                text = Str(msg, "content");
                reasoning = Str(msg, "reasoning_content") ?? Str(msg, "reasoning");
            }
            text ??= Str(c0, "text");
            return new ChatProbe(NullIfBlank(text), NullIfBlank(reasoning), finish);
        }
        catch (JsonException)
        {
            throw new ApiException("接口返回的不是合法 JSON，请检查 Base URL（应指向 OpenAI 兼容地址）。");
        }

        static string? Str(JsonElement el, string prop) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() : null;

        static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static string Trim(string s, int max)
    {
        s = string.Join(" ", s.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    // ---------- 模型列表（GET /models） ----------

    /// <summary>
    /// 拉取接口的可用模型列表：GET {BaseUrl}/models。
    /// 兼容 OpenAI 风格 {data:[{id}]} 与 Ollama 风格 {models:[{name/model}]}。
    /// </summary>
    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var url = BuildUrl("models");

        using var resp = await _http.GetAsync(new Uri(url, UriKind.Absolute), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new ApiException(BuildError((int)resp.StatusCode, body), (int)resp.StatusCode);

        var list = ParseModelIds(body);
        if (list.Count == 0)
            throw new ApiException("接口返回成功，但没有解析到任何模型。请确认 Base URL 指向 OpenAI 兼容地址（通常以 /v1 结尾）。");

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>从 /models 响应中解析模型 ID（多种常见格式）。</summary>
    internal static List<string> ParseModelIds(string body)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // 形如 { "data": [ { "id": "gpt-4o" }, ... ] }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
                Collect(list, data);

            // 形如 { "models": [ { "name": "llama3:latest" }, ... ] }（Ollama / 部分网关）
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var models))
                Collect(list, models);

            // 少数接口直接返回数组
            if (root.ValueKind == JsonValueKind.Array)
                Collect(list, root);
        }
        catch (JsonException)
        {
            throw new ApiException("接口返回的不是合法 JSON，请检查 Base URL（应指向 OpenAI 兼容的 /v1 地址）。");
        }
        return list;

        static void Collect(List<string> target, JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Array) return;
            foreach (var item in node.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    Add(target, item.GetString());
                    continue;
                }
                if (item.ValueKind != JsonValueKind.Object) continue;
                Add(target, Str(item, "id"));
                Add(target, Str(item, "name"));
                Add(target, Str(item, "model"));
            }
        }

        static string? Str(JsonElement el, string prop) =>
            el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        static void Add(List<string> target, string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            id = id.Trim();
            if (!target.Contains(id, StringComparer.OrdinalIgnoreCase)) target.Add(id);
        }
    }

    private static string Finish(string s)
    {
        s = s.Trim();
        if (string.IsNullOrWhiteSpace(s))
            throw new ApiException("API 响应中没有生成内容，请检查模型名称与请求参数。");
        return s;
    }

    // ---------- 流式请求体 ----------

    /// <summary>
    /// 读取 SSE 流。要点：
    /// ① 思考过程(reasoning_content/reasoning)与正文都作为增量实时产出，由调用方即时显示；
    /// ② 记录 finish_reason 与用量，便于判断「是不是被最大输出 Token 截断了」；
    /// ③ 客户端兜底：服务端若忽略 max_tokens，这里按增量条数（≈token 数）主动截断并给出 client_limit；
    /// ④ 空闲超时：超过「超时」秒数没有任何新数据就中止，避免界面无限转圈。
    /// </summary>
    /// <param name="disableThinking">
    /// 请求里带上关闭思考的参数组合（翻译场景更快、更省 token、首字更早）；
    /// 接口不认时自动降级，详见 <see cref="ThinkOffProfiles"/>。
    /// </param>
    private async IAsyncEnumerable<StreamDelta> CompleteStreamAsync(
        string system, string user, byte[]? imagePng,
        [EnumeratorCancellation] CancellationToken ct,
        bool disableThinking = false)
    {
        using var resp = await SendStreamRequestAsync(system, user, imagePng, ct, disableThinking);
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        int maxTokens = _cfg.MaxTokens;
        int approxTokens = 0;
        int idleMs = Math.Clamp(_cfg.TimeoutSeconds, 5, 600) * 1000;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            string? line;
            try
            {
                idle.CancelAfter(idleMs);              // 开始计时：这段时间内必须收到新数据
                line = await reader.ReadLineAsync(idle.Token);
                idle.CancelAfter(Timeout.Infinite);    // 收到数据，重新计时
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ApiException($"等待模型响应超时（{idleMs / 1000} 秒内没有新数据）。" +
                                       "可在首选项里调大「超时」，或改用非思考型模型。");
            }

            if (line is null) break; // 流结束
            if (line.Length == 0) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            var data = line.AsSpan(5).Trim().ToString();
            if (data.Length == 0) continue;
            if (data == "[DONE]") break;
            if (!TryParseDelta(data, out var delta)) continue;

            // 只有元信息（结束原因/用量）的增量也透传出去，供上层判断截断
            if (delta.Content is null && delta.Reasoning is null)
            {
                if (delta.FinishReason is not null || delta.CompletionTokens is not null) yield return delta;
                continue;
            }

            // 客户端兜底截断：多数服务端按 token 推送增量，因此增量条数≈已生成 token 数
            if (maxTokens > 0 && ++approxTokens > maxTokens)
            {
                Log.Info($"客户端兜底：已达到最大输出 Token（{maxTokens}），主动停止接收剩余输出");
                yield return new StreamDelta(null, null, "client_limit");
                break;
            }

            yield return delta;
        }
    }

    /// <summary>解析一条 SSE 数据行中的增量（正文/思考/结束原因/用量）。解析失败返回 false，不中断流。</summary>
    private static bool TryParseDelta(string data, out StreamDelta delta)
    {
        delta = default;
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return false;

            var c0 = choices[0];
            string? content = null, reasoning = null;

            if (c0.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                content = Str(d, "content");
                // 思考内容在不同接口里的字段名不一样：reasoning_content(DeepSeek/LM Studio)、reasoning(OpenRouter 等)、thinking
                reasoning = Str(d, "reasoning_content") ?? Str(d, "reasoning") ?? Str(d, "thinking");
            }

            var finish = Str(c0, "finish_reason");
            int? used = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                usage.TryGetProperty("completion_tokens", out var uct) && uct.ValueKind == JsonValueKind.Number &&
                uct.TryGetInt32(out var n))
                used = n;

            content = NullIfEmpty(content);
            reasoning = NullIfEmpty(reasoning);
            if (content is null && reasoning is null && finish is null && used is null) return false;

            delta = new StreamDelta(content, reasoning, finish, used);
            return true;
        }
        catch (JsonException)
        {
            // 部分兼容接口可能输出非 JSON 数据行，忽略继续
            return false;
        }

        static string? Str(JsonElement el, string prop) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() : null;

        static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>释放内部 HttpClient（客户端按「API + 模型」缓存，设置变化时会换新实例）。</summary>
    public void Dispose() => _http.Dispose();

    private static string BuildError(int statusCode, string body)
    {
        string err = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var er) &&
                er.TryGetProperty("message", out var m))
                err = m.GetString() ?? "";
        }
        catch { }
        if (string.IsNullOrWhiteSpace(err)) err = body.Length > 220 ? body[..220] : body;
        return $"API 请求失败 (HTTP {statusCode})：{err}";
    }

    private static ChatMessage BuildUserMessage(string user, byte[]? imagePng)
    {
        if (imagePng == null) return new ChatMessage { role = "user", content = user };

        return new ChatMessage
        {
            role = "user",
            content = new List<ContentPart>
            {
                new() { type = "text", text = user },
                new() { type = "image_url", image_url = new ImageUrl { url = $"data:image/png;base64,{Convert.ToBase64String(imagePng)}" } },
            },
        };
    }

    private sealed class ChatRequest
    {
        public string model { get; set; } = "";
        public List<ChatMessage> messages { get; set; } = new();
        public double? temperature { get; set; }
        public int? max_tokens { get; set; }
        /// <summary>OpenAI 新模型（o 系 / gpt-5）用它代替 max_tokens。</summary>
        public int? max_completion_tokens { get; set; }
        public bool stream { get; set; }
        /// <summary>LM Studio / OpenAI 风格：<c>"none"</c> 表示不思考。</summary>
        public string? reasoning_effort { get; set; }
        /// <summary>通义千问(DashScope) 等：false 表示关闭思考。</summary>
        public bool? enable_thinking { get; set; }
        /// <summary>vLLM / llama.cpp 等：把 enable_thinking 直接透传给对话模板。</summary>
        public ChatTemplateKwargs? chat_template_kwargs { get; set; }
        /// <summary>DeepSeek 等：{"type":"disabled"} 表示关闭思考。</summary>
        public ThinkingOption? thinking { get; set; }
    }

    private sealed class ThinkingOption
    {
        public string type { get; set; } = "disabled";
    }

    private sealed class ChatTemplateKwargs
    {
        public bool enable_thinking { get; set; }
    }

    private sealed class ChatMessage
    {
        public string role { get; set; } = "";
        public object? content { get; set; }
    }

    private sealed class ContentPart
    {
        public string type { get; set; } = "text";
        public string? text { get; set; }
        public ImageUrl? image_url { get; set; }
    }

    private sealed class ImageUrl
    {
        public string url { get; set; } = "";
    }
}
