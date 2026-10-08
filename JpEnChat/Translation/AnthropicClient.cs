using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace JpEnChat.Translation;

/// <summary>
/// Claude API (Messages API) client: one long-lived <see cref="HttpClient"/>, SSE streaming, prompt caching of the
/// built-in system prompt, structured output via <c>output_config.format</c>. Owned by the plugin and disposed on unload.
/// </summary>
/// <remarks>
/// <para><b>Request shape.</b> <c>system</c> is two text blocks: the built-in prompt with
/// <c>cache_control: {type: "ephemeral"}</c> (cached), then the player's glossary (not cached, omitted when empty).
/// <c>thinking</c> is never sent: current Claude models think adaptively by default and reject
/// <c>{type: "disabled"}</c> and <c>budget_tokens</c>; depth is controlled with <c>output_config.effort</c>.
/// <c>temperature</c> is never sent either (current models reject non-default sampling parameters).
/// <see cref="LlmRequest.FallbackModels"/> is ignored. For Sonnet 5.5 / Opus 5.5, when enabled, a policy refusal is
/// retried server-side on Anthropic's recommended model (<c>fallbacks: "default"</c>).</para>
/// <para><b>Timeouts.</b> Both calls stream. The configured timeout is an idle timeout (any SSE line resets it), tripled
/// until the first text arrives (silent thinking) and for <see cref="CompleteAsync"/> throughout.</para>
/// <para>The key is read on every request, sent only as the <c>x-api-key</c> header and never logged.</para>
/// </remarks>
public sealed class AnthropicClient : ILlmBackend, IDisposable
{
    public static readonly Uri BaseAddress = new("https://api.anthropic.com/v1/");

    public const string ApiVersion = "2023-06-01";

    /// <summary>Beta header value that enables the scalar <c>fallbacks: "default"</c> form.</summary>
    public const string FallbackBeta = "server-side-fallback-2026-07-01";

    private const int MaxErrorBodyChars = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Japanese as UTF-8 instead of \uXXXX escapes. Not HTML-embedded, so this is safe.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient http;
    private readonly Func<string> apiKey;
    private readonly Func<int> timeoutSeconds;
    private readonly Func<bool> refusalFallback;
    private readonly ILog log;

    /// <param name="apiKey">Returns the current key (e.g. <c>() =&gt; config.AnthropicKey</c>); empty = not set.</param>
    /// <param name="timeoutSeconds">Returns the current idle timeout in seconds.</param>
    /// <param name="refusalFallback">Whether to send <c>fallbacks: "default"</c> to Sonnet 5.5 / Opus 5.5.</param>
    /// <param name="log">Logger; never receives the key.</param>
    /// <param name="handler">Test seam. Null creates a <see cref="SocketsHttpHandler"/>.</param>
    public AnthropicClient(Func<string> apiKey, Func<int> timeoutSeconds, Func<bool> refusalFallback, ILog log, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(timeoutSeconds);
        ArgumentNullException.ThrowIfNull(refusalFallback);
        ArgumentNullException.ThrowIfNull(log);
        this.apiKey = apiKey;
        this.timeoutSeconds = timeoutSeconds;
        this.refusalFallback = refusalFallback;
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
        http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
    }

    /// <summary>Whether <paramref name="model"/> gets <c>fallbacks: "default"</c> (Sonnet 5.5 and Opus 5.5; never Haiku).</summary>
    public static bool SupportsRefusalFallback(string model) =>
        model.StartsWith("claude-sonnet-5-5", StringComparison.Ordinal)
        || model.StartsWith("claude-opus-5-5", StringComparison.Ordinal);

    /// <summary>Streams the text of the answer (<c>text_delta</c> only; thinking is skipped).</summary>
    /// <exception cref="AnthropicException">Non-2xx status or an <c>error</c> event.</exception>
    /// <exception cref="LlmRefusalException"><c>stop_reason: "refusal"</c> (after any partial text was yielded).</exception>
    public async IAsyncEnumerable<string> StreamAsync(
        LlmRequest req,
        [EnumeratorCancellation] CancellationToken ct = default,
        Action<LlmTiming>? onTiming = null)
    {
        await foreach (var text in StreamCoreAsync(req, tightAfterFirstText: true, ct, onTiming).ConfigureAwait(false))
        {
            yield return text;
        }
    }

    /// <summary>Streams internally and returns the concatenated text, so long thinking never hits a total deadline.</summary>
    /// <exception cref="AnthropicException">Non-2xx status, an <c>error</c> event, or no text at all.</exception>
    /// <exception cref="LlmRefusalException"><c>stop_reason: "refusal"</c>.</exception>
    public async Task<string> CompleteAsync(LlmRequest req, CancellationToken ct = default, Action<LlmTiming>? onTiming = null)
    {
        var sb = new StringBuilder();
        LlmTiming? timing = null;
        await foreach (var text in StreamCoreAsync(req, tightAfterFirstText: false, ct, t => timing = t).ConfigureAwait(false))
        {
            sb.Append(text);
        }

        if (timing is not null)
        {
            onTiming?.Invoke(timing);
        }

        if (sb.Length == 0)
        {
            throw new AnthropicException(null, $"Model returned no text (stop_reason {timing?.FinishReason ?? "none"}).");
        }

        return sb.ToString();
    }

    public void Dispose() => http.Dispose();

    /// <summary>Serializes <paramref name="req"/> to the Messages API wire format. Public for tests and debug logging.</summary>
    public static string SerializeBody(LlmRequest req, bool stream, bool refusalFallback)
    {
        ArgumentNullException.ThrowIfNull(req);
        var system = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = req.SystemPrompt,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            },
        };
        var suffix = req.SystemSuffix.Trim();
        if (suffix.Length > 0)
        {
            system.Add(new JsonObject { ["type"] = "text", ["text"] = suffix });
        }

        var body = new JsonObject
        {
            ["model"] = req.Model,
            ["max_tokens"] = req.MaxTokens ?? 4096,
            ["system"] = system,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = req.UserContent },
            },
        };

        if (stream)
        {
            body["stream"] = true;
        }

        var outputConfig = new JsonObject();
        if (!string.IsNullOrWhiteSpace(req.Effort))
        {
            outputConfig["effort"] = req.Effort.Trim();
        }

        if (req.JsonSchema is { } schema)
        {
            outputConfig["format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["schema"] = schema.Schema.DeepClone(),
            };
        }

        if (outputConfig.Count > 0)
        {
            body["output_config"] = outputConfig;
        }

        if (refusalFallback && SupportsRefusalFallback(req.Model))
        {
            body["fallbacks"] = "default";
        }

        return body.ToJsonString(JsonOptions);
    }

    private async IAsyncEnumerable<string> StreamCoreAsync(
        LlmRequest req,
        bool tightAfterFirstText,
        [EnumeratorCancellation] CancellationToken ct,
        Action<LlmTiming>? onTiming)
    {
        ArgumentNullException.ThrowIfNull(req);
        var idle = CurrentTimeout();
        var startIdle = idle * 3; // thinking can be silent for a while before the first text
        var afterFirstIdle = tightAfterFirstText ? idle : startIdle;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(startIdle);

        var sw = Stopwatch.StartNew();
        var fallback = SafeRefusalFallback() && SupportsRefusalFallback(req.Model);
        using var request = BuildRequest(req, fallback);
        using var response = await SendAsync(request, timeoutCts, ct).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
        using var reader = new StreamReader(body, Encoding.UTF8);

        var state = new StreamState();
        while (!state.Stopped)
        {
            var line = await ReadLineAsync(reader, timeoutCts, ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            timeoutCts.CancelAfter(state.FirstTokenMs is null ? startIdle : afterFirstIdle);
            if (line.Length == 0 || line[0] == ':' || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue; // "event:" lines are redundant with the payload's "type"
            }

            var text = ParseEvent(line.AsSpan(5).Trim(), state);
            if (!string.IsNullOrEmpty(text))
            {
                state.FirstTokenMs ??= sw.Elapsed.TotalMilliseconds;
                yield return text;
            }
        }

        var timing = new LlmTiming(
            state.Model,
            "Anthropic",
            state.FirstTokenMs,
            sw.Elapsed.TotalMilliseconds,
            state.InputTokens is null ? null : state.InputTokens + (state.CacheRead ?? 0) + (state.CacheWrite ?? 0),
            state.CacheRead,
            state.OutputTokens,
            state.StopReason,
            state.CacheWrite);
        log.Information(
            $"Claude API {state.Model ?? req.Model}: input {state.InputTokens?.ToString() ?? "?"} uncached + " +
            $"cache_read_input_tokens {state.CacheRead?.ToString() ?? "?"} + cache_creation_input_tokens {state.CacheWrite?.ToString() ?? "?"}, " +
            $"output {state.OutputTokens?.ToString() ?? "?"}, stop {state.StopReason ?? "?"}, {sw.ElapsedMilliseconds} ms.");

        if (state.StopReason == "refusal")
        {
            throw new LlmRefusalException();
        }

        onTiming?.Invoke(timing);
    }

    private bool SafeRefusalFallback()
    {
        try
        {
            return refusalFallback();
        }
        catch (Exception)
        {
            return false;
        }
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

    private HttpRequestMessage BuildRequest(LlmRequest req, bool fallback)
    {
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new MissingApiKeyException("No Anthropic API key is set.");
        }

        var json = SerializeBody(req, stream: true, fallback);
        log.Debug($"POST {BaseAddress}messages (x-api-key: <redacted>{(fallback ? ", anthropic-beta: " + FallbackBeta : string.Empty)}) {json}");

        var request = new HttpRequestMessage(HttpMethod.Post, "messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", key.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (fallback)
        {
            request.Headers.Add("anthropic-beta", FallbackBeta);
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
            throw new TimeoutException("Claude API request timed out.");
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
            throw new TimeoutException("Claude API stream stalled.");
        }
        catch (IOException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException("Claude API stream stalled.");
        }
    }

    /// <summary>Builds the exception for a non-2xx response from its <c>{"type":"error","error":{...}}</c> body.</summary>
    internal static AnthropicException CreateHttpError(HttpResponseMessage response, string body)
    {
        var status = (int)response.StatusCode;
        TimeSpan? retryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } d } => d,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null,
        };

        string? message = null;
        string? type = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body.Length > MaxErrorBodyChars ? body[..MaxErrorBodyChars] : body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    (type, message) = ReadError(error);
                }
            }
            catch (JsonException)
            {
                // Not JSON (e.g. an HTML error page from a proxy); fall back to the reason phrase.
            }
        }

        var shown = message ?? response.ReasonPhrase ?? "HTTP error";
        return new AnthropicException(status, $"HTTP {status}: {Truncate(shown)}", type, retryAfter)
        {
            ApiMessage = message is null ? null : Truncate(message),
        };
    }

    private static (string? Type, string? Message) ReadError(JsonElement error) =>
        (ReadString(error, "type"), ReadString(error, "message"));

    /// <summary>HTTP-like status for a mid-stream <c>error</c> event's type.</summary>
    internal static int? StatusForErrorType(string? type) => type switch
    {
        "invalid_request_error" => 400,
        "authentication_error" => 401,
        "billing_error" => 402,
        "permission_error" => 403,
        "not_found_error" => 404,
        "request_too_large" => 413,
        "rate_limit_error" => 429,
        "api_error" => 500,
        "overloaded_error" => 529,
        _ => null,
    };

    /// <summary>Parses one SSE <c>data:</c> payload. Returns text to yield (possibly null) and updates the state.</summary>
    private static string? ParseEvent(ReadOnlySpan<char> data, StreamState state)
    {
        try
        {
            using var doc = JsonDocument.Parse(data.ToString());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            switch (ReadString(root, "type"))
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                    {
                        state.Model ??= ReadString(message, "model");
                        ReadUsage(message, state);
                    }

                    return null;

                case "content_block_delta":
                    if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
                        ReadString(delta, "type") == "text_delta")
                    {
                        return ReadString(delta, "text");
                    }

                    return null; // thinking_delta, signature_delta, input_json_delta, ...

                case "message_delta":
                    if (root.TryGetProperty("delta", out var md) && md.ValueKind == JsonValueKind.Object &&
                        ReadString(md, "stop_reason") is { } stop)
                    {
                        state.StopReason = stop;
                    }

                    ReadUsage(root, state);
                    return null;

                case "message_stop":
                    state.Stopped = true;
                    return null;

                case "error":
                    string? type = null;
                    string? msg = null;
                    if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                    {
                        (type, msg) = ReadError(error);
                    }

                    throw new AnthropicException(StatusForErrorType(type), Truncate(msg ?? "Stream error"), type)
                    {
                        ApiMessage = msg is null ? null : Truncate(msg),
                    };

                default:
                    return null; // ping, content_block_start/stop
            }
        }
        catch (JsonException ex)
        {
            throw new AnthropicException("Claude API sent a malformed stream event.", ex);
        }
    }

    private static void ReadUsage(JsonElement owner, StreamState state)
    {
        if (!owner.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        state.InputTokens = ReadInt(usage, "input_tokens") ?? state.InputTokens;
        state.CacheRead = ReadInt(usage, "cache_read_input_tokens") ?? state.CacheRead;
        state.CacheWrite = ReadInt(usage, "cache_creation_input_tokens") ?? state.CacheWrite;
        state.OutputTokens = ReadInt(usage, "output_tokens") ?? state.OutputTokens;
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private sealed class StreamState
    {
        public string? Model { get; set; }

        public double? FirstTokenMs { get; set; }

        public int? InputTokens { get; set; }

        public int? CacheRead { get; set; }

        public int? CacheWrite { get; set; }

        public int? OutputTokens { get; set; }

        public string? StopReason { get; set; }

        public bool Stopped { get; set; }
    }
}
