using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace JpEnChat.Translation;

/// <summary>One OpenRouter chat-completion request (system + user message). Serialized by <see cref="OpenRouterClient"/>.</summary>
public sealed record ChatRequest
{
    /// <summary>Primary model id, e.g. <c>google/gemini-3.8-flash</c>.</summary>
    public required string Model { get; init; }

    /// <summary>Models to try after <see cref="Model"/>; sent as <c>models: [Model, ...FallbackModels]</c>. Empty = omit.</summary>
    public IReadOnlyList<string> FallbackModels { get; init; } = [];

    public required string SystemPrompt { get; init; }

    public required string UserContent { get; init; }

    public double? Temperature { get; init; }

    public int? MaxTokens { get; init; }

    /// <summary><c>reasoning.effort</c>; null or empty omits the whole <c>reasoning</c> object.</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary><c>reasoning.exclude</c>: keep thought tokens out of the response. Only sent with an effort.</summary>
    public bool ExcludeReasoning { get; init; } = true;

    /// <summary><c>provider.sort</c>; null omits it.</summary>
    public string? ProviderSort { get; init; } = "latency";

    /// <summary><c>response_format</c> (e.g. a json_schema object). Serialized by runtime type; null omits it.</summary>
    public object? ResponseFormat { get; init; }

    /// <summary><c>provider.require_parameters</c>: only route to endpoints that support every parameter sent.</summary>
    public bool RequireParameters { get; init; }
}

/// <summary>
/// Thin OpenRouter chat-completions client: one long-lived <see cref="HttpClient"/>, SSE streaming,
/// structured error mapping. Owned by the plugin for its whole lifetime and disposed on unload.
/// </summary>
/// <remarks>
/// <para>Timeouts: <see cref="HttpClient.Timeout"/> is infinite; each call enforces the configured
/// <c>timeoutSeconds</c> itself and throws <see cref="TimeoutException"/> when it expires. For streaming it is an
/// idle timeout (reset by every SSE line, including keep-alive comments), so a long batch that keeps streaming is
/// never cut off. For <see cref="CompleteAsync"/> it is a total deadline of twice the configured value, because a
/// structured EN→JA answer with reasoning cannot stream partial progress.</para>
/// <para>The API key is read from the provider on every request (so config edits apply live), sent only as the
/// <c>Authorization</c> header, and never logged. Request bodies are logged at Debug level only.</para>
/// </remarks>
public sealed class OpenRouterClient : ILlmBackend, IDisposable
{
    public static readonly Uri BaseAddress = new("https://openrouter.ai/api/v1/");

    private const string Referer = "https://github.com/colliexiv/ffxivjpentranslate";
    private const string AppTitle = "JP/EN Chat";
    private const int MaxErrorBodyChars = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Japanese as UTF-8 instead of \uXXXX escapes (a third of the bytes). Not HTML-embedded, so this is safe.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient http;
    private readonly Func<string> apiKey;
    private readonly Func<int> timeoutSeconds;
    private readonly ILog log;

    /// <param name="apiKey">Returns the current key (e.g. <c>() =&gt; config.OpenRouterKey</c>); empty = not set.</param>
    /// <param name="timeoutSeconds">Returns the current per-request timeout (e.g. <c>config.RequestTimeoutSeconds</c>).</param>
    /// <param name="log">Logger; never receives the key.</param>
    /// <param name="handler">Test seam. Null creates a <see cref="SocketsHttpHandler"/>.</param>
    public OpenRouterClient(Func<string> apiKey, Func<int> timeoutSeconds, ILog log, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(timeoutSeconds);
        ArgumentNullException.ThrowIfNull(log);
        this.apiKey = apiKey;
        this.timeoutSeconds = timeoutSeconds;
        this.log = log;

        handler ??= new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
        };

        http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.Add("HTTP-Referer", Referer);
        http.DefaultRequestHeaders.Add("X-Title", AppTitle);
    }

    /// <summary>
    /// Streams <c>choices[0].delta.content</c> strings. Completes at <c>data: [DONE]</c> or end of stream.
    /// </summary>
    /// <param name="onTiming">Invoked once after a successful stream with timing and usage.</param>
    /// <exception cref="OpenRouterException">Non-2xx status, error chunk, or <c>finish_reason: "error"</c>.</exception>
    /// <exception cref="TimeoutException">No data for the configured timeout.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async IAsyncEnumerable<string> StreamChatAsync(
        ChatRequest req,
        [EnumeratorCancellation] CancellationToken ct = default,
        Action<LlmTiming>? onTiming = null)
    {
        ArgumentNullException.ThrowIfNull(req);
        var timeout = CurrentTimeout();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var sw = Stopwatch.StartNew();
        using var request = BuildRequest(req, stream: true);
        using var response = await SendAsync(request, timeoutCts, ct).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
        using var reader = new StreamReader(body, Encoding.UTF8);

        var state = new StreamState();
        while (true)
        {
            var line = await ReadLineAsync(reader, timeoutCts, ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            timeoutCts.CancelAfter(timeout); // idle timeout: any line, including ": keep-alive", resets it
            if (line.Length == 0 || line[0] == ':')
            {
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue; // "event:", "id:", "retry:" — not used by OpenRouter
            }

            var data = line.AsSpan(5).Trim();
            if (data.SequenceEqual("[DONE]"))
            {
                break;
            }

            var content = ParseChunk(data, state);
            if (!string.IsNullOrEmpty(content))
            {
                state.FirstTokenMs ??= sw.Elapsed.TotalMilliseconds;
                yield return content;
            }
        }

        onTiming?.Invoke(new LlmTiming(
            state.Model, state.Provider, state.FirstTokenMs, sw.Elapsed.TotalMilliseconds,
            state.PromptTokens, state.CachedTokens, state.CompletionTokens, state.FinishReason));
    }

    /// <summary>Non-streaming completion; returns <c>choices[0].message.content</c>.</summary>
    /// <exception cref="OpenRouterException">Non-2xx status, error body, empty content, or <c>finish_reason: "error"</c>.</exception>
    /// <exception cref="TimeoutException">No complete response within twice the configured timeout.</exception>
    public async Task<string> CompleteAsync(ChatRequest req, CancellationToken ct = default, Action<LlmTiming>? onTiming = null)
    {
        ArgumentNullException.ThrowIfNull(req);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CurrentTimeout() * 2);

        var sw = Stopwatch.StartNew();
        using var request = BuildRequest(req, stream: false);
        using var response = await SendAsync(request, timeoutCts, ct).ConfigureAwait(false);
        string json;
        try
        {
            json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("OpenRouter response timed out.");
        }

        var state = new StreamState();
        string? content;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            ThrowIfErrorObject(root);
            ReadMeta(root, state);
            content = null;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                ThrowIfFinishError(choice, state);
                if (choice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    content = c.GetString();
                }
            }
        }
        catch (JsonException ex)
        {
            throw new OpenRouterException("OpenRouter returned malformed JSON.", ex);
        }

        var total = sw.Elapsed.TotalMilliseconds;
        onTiming?.Invoke(new LlmTiming(
            state.Model, state.Provider, total, total, state.PromptTokens, state.CachedTokens, state.CompletionTokens, state.FinishReason));

        if (string.IsNullOrEmpty(content))
        {
            throw new OpenRouterException(null, $"Model returned no content (finish_reason {state.FinishReason ?? "none"}).");
        }

        return content;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<string> StreamAsync(LlmRequest req, CancellationToken ct, Action<LlmTiming>? onTiming) =>
        StreamChatAsync(ToChatRequest(req), ct, onTiming);

    /// <inheritdoc/>
    public Task<string> CompleteAsync(LlmRequest req, CancellationToken ct, Action<LlmTiming>? onTiming) =>
        CompleteAsync(ToChatRequest(req), ct, onTiming);

    /// <summary>
    /// Maps a provider-neutral request to OpenRouter's: one system message (<see cref="LlmRequest.FullSystemPrompt"/>),
    /// <see cref="LlmRequest.Effort"/> → <c>reasoning.effort</c> (thoughts excluded from the answer), a JSON schema →
    /// strict <c>response_format</c> plus <c>provider.require_parameters</c>, and latency-sorted routing.
    /// </summary>
    public static ChatRequest ToChatRequest(LlmRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        return new ChatRequest
        {
            Model = req.Model,
            FallbackModels = req.FallbackModels,
            SystemPrompt = req.FullSystemPrompt,
            UserContent = req.UserContent,
            Temperature = req.Temperature,
            MaxTokens = req.MaxTokens,
            ReasoningEffort = string.IsNullOrWhiteSpace(req.Effort) ? null : req.Effort.Trim(),
            ExcludeReasoning = true,
            ProviderSort = "latency",
            ResponseFormat = req.JsonSchema is { } schema ? ResponseFormatFor(schema) : null,
            RequireParameters = req.JsonSchema is not null,
        };
    }

    /// <summary>OpenRouter's strict <c>response_format</c> object for <paramref name="schema"/>.</summary>
    public static JsonObject ResponseFormatFor(LlmJsonSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return new JsonObject
        {
            ["type"] = "json_schema",
            ["json_schema"] = new JsonObject
            {
                ["name"] = schema.Name,
                ["strict"] = true,
                ["schema"] = schema.Schema.DeepClone(),
            },
        };
    }

    public void Dispose() => http.Dispose();

    /// <summary>Serializes <paramref name="req"/> to the OpenRouter wire format. Public for tests and debug logging.</summary>
    public static string SerializeBody(ChatRequest req, bool stream)
    {
        ArgumentNullException.ThrowIfNull(req);
        List<string>? models = null;
        if (req.FallbackModels.Count > 0)
        {
            models = [req.Model];
            foreach (var m in req.FallbackModels)
            {
                if (!string.IsNullOrWhiteSpace(m) && !models.Contains(m, StringComparer.Ordinal))
                {
                    models.Add(m);
                }
            }

            if (models.Count == 1)
            {
                models = null;
            }
        }

        var body = new WireRequest
        {
            Model = req.Model,
            Models = models,
            Messages =
            [
                new WireMessage("system", req.SystemPrompt),
                new WireMessage("user", req.UserContent),
            ],
            Temperature = req.Temperature,
            MaxTokens = req.MaxTokens,
            Stream = stream ? true : null,
            Reasoning = string.IsNullOrEmpty(req.ReasoningEffort)
                ? null
                : new WireReasoning(req.ReasoningEffort, req.ExcludeReasoning ? true : null),
            Provider = new WireProvider
            {
                Sort = req.ProviderSort,
                AllowFallbacks = true,
                DataCollection = "deny",
                RequireParameters = req.RequireParameters ? true : null,
            },
            ResponseFormat = req.ResponseFormat,
        };
        return JsonSerializer.Serialize(body, JsonOptions);
    }

    private TimeSpan CurrentTimeout()
    {
        int seconds;
        try
        {
            seconds = timeoutSeconds();
        }
        catch (Exception)
        {
            seconds = 0;
        }

        return TimeSpan.FromSeconds(Math.Clamp(seconds <= 0 ? 8 : seconds, 1, 300));
    }

    private HttpRequestMessage BuildRequest(ChatRequest req, bool stream)
    {
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new MissingApiKeyException();
        }

        var json = SerializeBody(req, stream);
        log.Debug($"POST {BaseAddress}chat/completions (Authorization: Bearer <redacted>) {json}");

        var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        if (stream)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationTokenSource timeoutCts, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("OpenRouter request timed out.");
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                body = string.Empty;
            }

            throw CreateHttpError(response, body);
        }
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationTokenSource timeoutCts, CancellationToken ct)
    {
        try
        {
            return await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("OpenRouter stream stalled.");
        }
        catch (IOException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException("OpenRouter stream stalled.");
        }
    }

    private static OpenRouterException CreateHttpError(HttpResponseMessage response, string body)
    {
        var status = (int)response.StatusCode;
        TimeSpan? retryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } d } => d,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null,
        };

        string? message = null;
        string? limitSource = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body.Length > MaxErrorBodyChars ? body[..MaxErrorBodyChars] : body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    (message, limitSource) = ReadErrorObject(error);
                }
            }
            catch (JsonException)
            {
                // Not JSON (e.g. an HTML error page from a proxy); fall back to the reason phrase.
            }
        }

        message ??= response.ReasonPhrase ?? "HTTP error";
        return new OpenRouterException(status, $"HTTP {status}: {Truncate(message)}", limitSource, retryAfter);
    }

    private static (string? Message, string? LimitSource) ReadErrorObject(JsonElement error)
    {
        string? message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        string? limitSource = null;
        if (error.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object &&
            meta.TryGetProperty("limit_source", out var ls) && ls.ValueKind == JsonValueKind.String)
        {
            limitSource = ls.GetString();
        }

        return (message, limitSource);
    }

    private static int? ReadErrorCode(JsonElement error)
    {
        if (!error.TryGetProperty("code", out var code))
        {
            return null;
        }

        return code.ValueKind switch
        {
            JsonValueKind.Number when code.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(code.GetString(), out var n) => n,
            _ => null,
        };
    }

    /// <summary>Parses one SSE <c>data:</c> payload. Returns the content delta (possibly null) and updates metadata.</summary>
    private static string? ParseChunk(ReadOnlySpan<char> data, StreamState state)
    {
        try
        {
            using var doc = JsonDocument.Parse(data.ToString());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            ThrowIfErrorObject(root);
            ReadMeta(root, state);

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var choice = choices[0];
            ThrowIfFinishError(choice, state);
            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
                delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }

            return null;
        }
        catch (JsonException ex)
        {
            throw new OpenRouterException("OpenRouter sent a malformed stream chunk.", ex);
        }
    }

    private static void ThrowIfErrorObject(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var (message, limitSource) = ReadErrorObject(error);
            throw new OpenRouterException(ReadErrorCode(error), Truncate(message ?? "Provider error"), limitSource);
        }
    }

    private static void ThrowIfFinishError(JsonElement choice, StreamState state)
    {
        if (!choice.TryGetProperty("finish_reason", out var fr) || fr.ValueKind != JsonValueKind.String)
        {
            return;
        }

        state.FinishReason = fr.GetString();
        if (state.FinishReason == "error")
        {
            string? message = null;
            int? code = null;
            if (choice.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                message = ReadErrorObject(error).Message;
                code = ReadErrorCode(error);
            }

            throw new OpenRouterException(code, Truncate(message ?? "Provider stopped with finish_reason \"error\"."));
        }
    }

    private static void ReadMeta(JsonElement root, StreamState state)
    {
        if (state.Model is null && root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
        {
            state.Model = model.GetString();
        }

        if (state.Provider is null && root.TryGetProperty("provider", out var provider) && provider.ValueKind == JsonValueKind.String)
        {
            state.Provider = provider.GetString();
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            state.PromptTokens = ReadInt(usage, "prompt_tokens") ?? state.PromptTokens;
            state.CompletionTokens = ReadInt(usage, "completion_tokens") ?? state.CompletionTokens;
            if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
            {
                state.CachedTokens = ReadInt(details, "cached_tokens") ?? state.CachedTokens;
            }
        }
    }

    private static int? ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private sealed class StreamState
    {
        public string? Model { get; set; }

        public string? Provider { get; set; }

        public double? FirstTokenMs { get; set; }

        public int? PromptTokens { get; set; }

        public int? CachedTokens { get; set; }

        public int? CompletionTokens { get; set; }

        public string? FinishReason { get; set; }
    }

    private sealed class WireRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("models")]
        public List<string>? Models { get; init; }

        [JsonPropertyName("messages")]
        public required WireMessage[] Messages { get; init; }

        [JsonPropertyName("temperature")]
        public double? Temperature { get; init; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; init; }

        [JsonPropertyName("stream")]
        public bool? Stream { get; init; }

        [JsonPropertyName("reasoning")]
        public WireReasoning? Reasoning { get; init; }

        [JsonPropertyName("provider")]
        public WireProvider? Provider { get; init; }

        [JsonPropertyName("response_format")]
        public object? ResponseFormat { get; init; }
    }

    private sealed record WireMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record WireReasoning(
        [property: JsonPropertyName("effort")] string Effort,
        [property: JsonPropertyName("exclude")] bool? Exclude);

    private sealed class WireProvider
    {
        [JsonPropertyName("sort")]
        public string? Sort { get; init; }

        [JsonPropertyName("allow_fallbacks")]
        public bool? AllowFallbacks { get; init; }

        [JsonPropertyName("data_collection")]
        public string? DataCollection { get; init; }

        [JsonPropertyName("require_parameters")]
        public bool? RequireParameters { get; init; }
    }
}
