using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

public sealed class ApiException : Exception
{
    public int StatusCode { get; }
    public ApiException(string message, int statusCode = 0) : base(message) { StatusCode = statusCode; }
}

/// <summary>一次流式增量：思考内容(reasoning) 与 正文(content) 可分别出现。</summary>
public readonly record struct StreamDelta(string? Content, string? Reasoning);

/// <summary>一次非流式探测的结果。</summary>
internal readonly record struct ChatProbe(string? Content, string? Reasoning, string? FinishReason);

/// <summary>OpenAI 兼容 Chat Completions 客户端（支持流式输出与视觉模型输入）。</summary>
public sealed class OpenAIClient
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
    /// 翻译时关闭思考：请求里带 <c>"thinking":{"type":"disabled"}</c>（模型支持时思考为 0，直出译文、流式更快），
    /// 同时不展示 reasoning。生成回复始终保留思考过程。
    /// </summary>
    public bool SkipReasoningForTranslation { get; set; } = true;

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

        if (!string.IsNullOrWhiteSpace(probe.Content))
            parts.Add($"模型 {_cfg.Model} 正常应答：{Trim(probe.Content!, 60)}");
        else if (!string.IsNullOrWhiteSpace(probe.Reasoning))
            parts.Add($"模型 {_cfg.Model} 有响应，但回答全部都落在「思考过程」里（finish_reason={probe.FinishReason ?? "?"}）——" +
                      "这是思考型模型，正式翻译时请把「最大输出 Token」调大一些");
        else
            parts.Add($"模型 {_cfg.Model} 返回了空内容（finish_reason={probe.FinishReason ?? "?"}），请检查模型名与请求参数");

        return string.Join("；", parts);
    }

    /// <summary>最小对话探测（同步返回，非流式）。</summary>
    /// <summary>
    /// 发送流式对话请求。带关闭思考参数时，若接口返回 400（不支持该参数）则去掉参数重试一次。
    /// </summary>
    private async Task<HttpResponseMessage> SendStreamRequestAsync(
        string system, string user, byte[]? imagePng, CancellationToken ct, bool disableThinking)
    {
        var url = (_cfg.BaseUrl ?? "").TrimEnd('/') + "/chat/completions";
        if (string.IsNullOrWhiteSpace(_cfg.BaseUrl))
            throw new ApiException("请先填写接口地址 (Base URL)。");

        var resp = await SendOnceAsync(url, system, user, imagePng, ct, disableThinking);
        if (resp.IsSuccessStatusCode)
        {
            if (disableThinking) Log.Info("请求参数: thinking.type=disabled（不思考，直出结果）");
            return resp;
        }

        var code = (int)resp.StatusCode;
        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.Dispose();

        if (disableThinking && code == 400)
        {
            Log.Info($"接口不支持关闭思考参数（HTTP 400），已去掉 thinking 后重试：{Trim(body, 120)}");
            resp = await SendOnceAsync(url, system, user, imagePng, ct, false);
            if (resp.IsSuccessStatusCode) return resp;
            code = (int)resp.StatusCode;
            body = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
        }

        throw new ApiException(BuildError(code, body), code);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        string url, string system, string user, byte[]? imagePng, CancellationToken ct, bool disableThinking)
    {
        var req = new ChatRequest
        {
            model = _cfg.Model,
            temperature = _cfg.Temperature,
            max_tokens = _cfg.MaxTokens,
            stream = true,
            thinking = disableThinking ? new ThinkingOption() : null,
            messages =
            {
                new ChatMessage { role = "system", content = system },
                BuildUserMessage(user, imagePng),
            },
        };

        using var content = new StringContent(JsonSerializer.Serialize(req, JsonOpts), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Absolute)) { Content = content };
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>最小对话探测（非流式，同样优先关闭思考，接口不支持时自动去掉参数重试）。</summary>
    private async Task<ChatProbe> ProbeChatAsync(CancellationToken ct)
    {
        var url = (_cfg.BaseUrl ?? "").TrimEnd('/') + "/chat/completions";
        var (code, body) = await ProbeOnceAsync(url, ct, true);
        if (code == 400)
        {
            Log.Info($"探测：接口不支持关闭思考参数，已去掉 thinking 重试：{Trim(body, 120)}");
            (code, body) = await ProbeOnceAsync(url, ct, false);
        }
        if (code is < 200 or > 299)
            throw new ApiException(BuildError(code, body), code);
        return ParseProbe(body);
    }

    private async Task<(int Code, string Body)> ProbeOnceAsync(string url, CancellationToken ct, bool disableThinking)
    {
        var req = new ChatRequest
        {
            model = _cfg.Model,
            temperature = _cfg.Temperature,
            max_tokens = 64,
            thinking = disableThinking ? new ThinkingOption() : null,
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

        using var content = new StringContent(JsonSerializer.Serialize(req, JsonOpts), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(new Uri(url, UriKind.Absolute), content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return ((int)resp.StatusCode, body);
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
        var url = (_cfg.BaseUrl ?? "").TrimEnd('/') + "/models";
        if (string.IsNullOrWhiteSpace(_cfg.BaseUrl))
            throw new ApiException("请先填写接口地址 (Base URL)。");

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
    /// 思考过程(reasoning_content)始终会作为增量返回，由调用方决定是否展示：
    /// 有些思考型模型会把全部 token 用在思考上、正文为空，此时必须能看到思考内容才不会误判为“请求失败”。
    /// </summary>
    /// <param name="disableThinking">
    /// 发送 <c>"thinking":{"type":"disabled"}</c> 让思考型模型直接作答（翻译场景更快、更省 token）。
    /// 接口不认识该参数（HTTP 400）时会自动去掉它重试一次，保证兼容性。
    /// </param>
    private async IAsyncEnumerable<StreamDelta> CompleteStreamAsync(
        string system, string user, byte[]? imagePng,
        [EnumeratorCancellation] CancellationToken ct,
        bool disableThinking = false)
    {
        using var resp = await SendStreamRequestAsync(system, user, imagePng, ct, disableThinking);
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break; // 流结束
            if (line.Length == 0) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            var data = line.AsSpan(5).Trim().ToString();
            if (data.Length == 0) continue;
            if (data == "[DONE]") break;

            if (TryParseDelta(data, out var delta))
            {
                if (delta.Content is null && delta.Reasoning is null) continue;
                yield return delta;
            }
        }
    }

    /// <summary>解析一条 SSE 数据行中的增量（正文/思考）。解析失败返回 false，不中断流。</summary>
    private static bool TryParseDelta(string data, out StreamDelta delta)
    {
        delta = default;
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return false;
            if (!choices[0].TryGetProperty("delta", out var d)) return false;

            string? c = null, r = null;
            if (d.TryGetProperty("content", out var cp) && cp.ValueKind == JsonValueKind.String) c = cp.GetString();
            if (d.TryGetProperty("reasoning_content", out var rp) && rp.ValueKind == JsonValueKind.String) r = rp.GetString();
            if (c is null && r is null) return false;

            delta = new StreamDelta(c, r);
            return true;
        }
        catch (JsonException)
        {
            // 部分兼容接口可能输出非 JSON 数据行，忽略继续
            return false;
        }
    }

    // ---------- 非流式探测见 ProbeChatAsync / ParseProbe ----------

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
        public bool stream { get; set; }
        /// <summary>思考型模型开关：{"type":"disabled"} 表示关闭思考（deepseek 等支持）。</summary>
        public ThinkingOption? thinking { get; set; }
    }

    private sealed class ThinkingOption
    {
        public string type { get; set; } = "disabled";
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
