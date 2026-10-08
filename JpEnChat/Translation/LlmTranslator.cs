using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using JpEnChat.Models;

namespace JpEnChat.Translation;

/// <summary>
/// <see cref="ITranslator"/> over an <see cref="ILlmBackend"/> (PLAN §3.3, §4.1, §12): streamed numbered-line batches
/// for incoming chat, JSON-schema output for the outgoing EN→JA flow. The provider (OpenRouter or the Claude API) and
/// its models come from <see cref="Configuration"/> on every request.
/// </summary>
/// <remarks>
/// Reads <see cref="Configuration"/> on thread-pool threads. Only scalar properties and a defensive copy of
/// <see cref="Configuration.FallbackModels"/> are read, so a concurrent edit in the config window can at worst apply
/// one request late. When the backend is a <see cref="BackendSwitch"/>, the provider is read once per request and the
/// matching backend is used directly, so the model id and the service always agree.
/// </remarks>
public sealed class LlmTranslator : ITranslator
{
    /// <summary>Temperature for incoming batches (OpenRouter only; the Claude API gets none).</summary>
    public const double IncomingTemperature = 0.2;

    /// <summary>Temperature for the outgoing structured request (OpenRouter only).</summary>
    public const double OutgoingTemperature = 0.3;

    /// <summary>Claude API <c>max_tokens</c> for an incoming batch. Thinking counts against it, hence the headroom.</summary>
    public const int AnthropicIncomingMaxTokens = 4096;

    /// <summary>Claude API <c>max_tokens</c> for the outgoing request.</summary>
    public const int AnthropicOutgoingMaxTokens = 8192;

    /// <summary>Upper bound on the custom style text sent with an outgoing request, in characters.</summary>
    public const int MaxCustomStyleChars = 500;

    private static readonly JsonSerializerOptions ParseOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Configuration config;
    private readonly ILlmBackend backend;
    private readonly ILog log;

    public LlmTranslator(Configuration config, ILlmBackend backend, ILog log)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(log);
        this.config = config;
        this.backend = backend;
        this.log = log;
    }

    /// <summary>The outgoing JSON schema (PLAN §4.1): ja, segments[], back, style.</summary>
    public static LlmJsonSchema OutgoingSchema() => new("outgoing", new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["ja"] = new JsonObject { ["type"] = "string" },
            ["segments"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["ja"] = new JsonObject { ["type"] = "string" },
                        ["reading"] = new JsonObject { ["type"] = "string" },
                        ["en"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("ja", "reading", "en"),
                    ["additionalProperties"] = false,
                },
            },
            ["back"] = new JsonObject { ["type"] = "string" },
            ["style"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(Styles.Polite, Styles.Casual, Styles.Cool, Styles.Custom),
            },
        },
        ["required"] = new JsonArray("ja", "segments", "back", "style"),
        ["additionalProperties"] = false,
    });

    /// <summary>
    /// Builds the incoming user message: an optional <c>Target:</c> line, the context block when there is one
    /// (followed by <c>Translate:</c>), then the numbered lines. Newlines inside a message become spaces.
    /// </summary>
    public static string BuildBatchUserContent(IReadOnlyList<ChatLine> lines, Lang target, TranslationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var sb = new StringBuilder();
        if (target != Lang.En)
        {
            sb.Append("Target: ").Append(target == Lang.Ja ? "Japanese" : target.ToString()).Append('\n');
        }

        if (context is not null)
        {
            context.AppendTo(sb);
            sb.Append("Translate:\n");
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            sb.Append(i + 1).Append(": ").Append(OneLine(lines[i].Original));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the outgoing user message: the context block when there is one, the style section, then
    /// <c>Text: &lt;English&gt;</c>.
    /// </summary>
    public static string BuildOutgoingUserContent(string english, OutgoingStyle style, string? customStyleText, TranslationContext? context)
    {
        ArgumentNullException.ThrowIfNull(english);
        var sb = new StringBuilder();
        context?.AppendTo(sb);
        sb.Append(StyleSection(style, customStyleText)).Append('\n');
        sb.Append("Text: ").Append(OneLine(english).Trim());
        return sb.ToString();
    }

    /// <summary>
    /// <c>Style: &lt;name&gt; — &lt;instruction&gt;</c>. A <see cref="OutgoingStyle.Custom"/> style without text falls
    /// back to <see cref="OutgoingStyle.Polite"/>.
    /// </summary>
    public static string StyleSection(OutgoingStyle style, string? customStyleText)
    {
        var effective = EffectiveStyle(style, customStyleText);
        var instruction = effective == OutgoingStyle.Custom
            ? Prompts.CustomStyleInstruction(CleanCustomStyle(customStyleText))
            : Prompts.StyleInstruction(effective);
        return $"Style: {Styles.ToWire(effective)} — {instruction}";
    }

    /// <summary>The style actually requested: Custom with blank text is Polite.</summary>
    public static OutgoingStyle EffectiveStyle(OutgoingStyle style, string? customStyleText) =>
        Styles.Normalize(style) == OutgoingStyle.Custom && CleanCustomStyle(customStyleText).Length == 0
            ? OutgoingStyle.Polite
            : Styles.Normalize(style);

    /// <summary>The custom persona as sent: one line, trimmed, at most <see cref="MaxCustomStyleChars"/> characters.</summary>
    public static string CleanCustomStyle(string? text)
    {
        var s = OneLine(text ?? string.Empty).Trim();
        return s.Length <= MaxCustomStyleChars ? s : s[..MaxCustomStyleChars];
    }

    /// <summary>
    /// OpenRouter output budget. Generous on purpose: with reasoning models (Gemini 3.x thinks even at "low") reasoning
    /// tokens count against <c>max_tokens</c>, and a tight cap truncates the visible answer. Billing is per
    /// token actually generated, so the headroom costs nothing.
    /// </summary>
    public static int BatchMaxTokens(IReadOnlyList<ChatLine> lines)
    {
        var chars = 0;
        foreach (var l in lines)
        {
            chars += l.Original.Length;
        }

        return Math.Clamp(1024 + (chars * 3) + (lines.Count * 8), 1024, 4096);
    }

    /// <summary>The incoming batch request for <paramref name="provider"/>.</summary>
    public LlmRequest BuildBatchRequest(LlmProvider provider, IReadOnlyList<ChatLine> lines, Lang target, TranslationContext? context)
    {
        var anthropic = provider == LlmProvider.Anthropic;
        return new LlmRequest
        {
            Model = anthropic ? config.AnthropicModel : config.Model,
            FallbackModels = anthropic ? [] : SnapshotFallbacks(),
            SystemPrompt = Prompts.IncomingSystem,
            SystemSuffix = Prompts.IncomingGlossarySuffix(config.UserGlossary),
            UserContent = BuildBatchUserContent(lines, target, context),
            Temperature = anthropic ? null : IncomingTemperature,
            MaxTokens = anthropic ? AnthropicIncomingMaxTokens : BatchMaxTokens(lines),
            Effort = anthropic ? Efforts.Low : NullIfEmpty(config.ReasoningEffort),
        };
    }

    /// <summary>The outgoing request for <paramref name="provider"/>.</summary>
    public LlmRequest BuildOutgoingRequest(LlmProvider provider, string userContent)
    {
        var anthropic = provider == LlmProvider.Anthropic;
        return new LlmRequest
        {
            Model = anthropic ? config.AnthropicOutgoingModel : config.OutgoingModel,
            SystemPrompt = Prompts.OutgoingSystem,
            SystemSuffix = Prompts.OutgoingGlossarySuffix(config.UserGlossary),
            UserContent = userContent,
            Temperature = anthropic ? null : OutgoingTemperature,
            MaxTokens = anthropic ? AnthropicOutgoingMaxTokens : 4096,
            Effort = anthropic ? NullIfEmpty(config.AnthropicOutgoingEffort) : NullIfEmpty(config.ReasoningEffort),
            JsonSchema = OutgoingSchema(),
        };
    }

    public async Task TranslateBatchAsync(
        IReadOnlyList<ChatLine> lines,
        Lang target,
        TranslationContext? context,
        IProgress<(long LineId, string Delta)> progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(progress);
        if (lines.Count == 0)
        {
            return;
        }

        var (service, provider) = Resolve();
        var request = BuildBatchRequest(provider, lines, target, context);

        var router = new NumberedLineRouter(lines.Select(l => l.Id).ToArray(), progress);
        LlmTiming? timing = null;
        await foreach (var delta in service.StreamAsync(request, ct, t => timing = t).ConfigureAwait(false))
        {
            router.Push(delta);
        }

        router.Complete();
        LogTiming(provider, "batch", lines.Count, request.Model, timing);

        var missing = router.MissingIds;
        if (missing.Count > 0)
        {
            throw new IncompleteBatchException(missing);
        }
    }

    public async Task<OutgoingDraft> TranslateOutgoingAsync(OutgoingDraft draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var english = OneLine(draft.EnglishText).Trim();
        if (english.Length == 0)
        {
            throw new ArgumentException("Nothing to translate.", nameof(draft));
        }

        var customText = config.CustomStyleText;
        var style = EffectiveStyle(draft.Style, customText);
        var maxBytes = Math.Max(IChatSender.MaxMessageBytes - Encoding.UTF8.GetByteCount(draft.ChannelPrefix), 1);
        var userContent = BuildOutgoingUserContent(english, style, customText, draft.Context);
        var (service, provider) = Resolve();

        var result = await RequestOutgoingAsync(service, provider, userContent, style, ct).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetByteCount(result.Ja);
        if (bytes > maxBytes)
        {
            log.Debug($"Outgoing translation is {bytes} bytes (limit {maxBytes}); asking for a shorter version.");
            var shorter = userContent +
                $"\nThe previous Japanese was {bytes} UTF-8 bytes, but the chat limit is {maxBytes} bytes " +
                $"(about {maxBytes / 3} Japanese characters). Write a shorter version with the same meaning.";
            result = await RequestOutgoingAsync(service, provider, shorter, style, ct).ConfigureAwait(false);
        }

        return draft with
        {
            JapaneseText = result.Ja,
            Segments = result.Segments,
            BackTranslation = result.Back,
            Style = result.Style,
        };
    }

    /// <summary>
    /// Parses the structured outgoing answer. Tolerates a surrounding code fence or text around the JSON object; falls
    /// back to the requested style when the model returns another value.
    /// </summary>
    /// <exception cref="LlmException">No valid JSON object, or <c>ja</c> is empty.</exception>
    public static (string Ja, IReadOnlyList<Segment> Segments, string Back, OutgoingStyle Style) ParseOutgoing(
        string content, OutgoingStyle requestedStyle)
    {
        ArgumentNullException.ThrowIfNull(content);
        var wire = DeserializeOutgoing(StripCodeFence(content));
        var ja = wire?.Ja?.Trim();
        if (string.IsNullOrEmpty(ja))
        {
            throw new LlmException(null, "Model returned an empty Japanese translation.");
        }

        var segments = (wire!.Segments ?? [])
            .Where(s => s is not null && !string.IsNullOrEmpty(s.Ja))
            .Select(s => new Segment(s!.Ja!, s.Reading ?? string.Empty, s.En ?? string.Empty))
            .ToArray();
        var style = Styles.FromWire(wire.Style) ?? requestedStyle;
        return (ja, segments, wire.Back?.Trim() ?? string.Empty, style);
    }

    private static OutgoingWire? DeserializeOutgoing(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OutgoingWire>(json, ParseOptions);
        }
        catch (JsonException ex)
        {
            // Some models wrap the object in prose; try the outermost {...}.
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start >= 0 && end > start && (start > 0 || end < json.Length - 1))
            {
                try
                {
                    return JsonSerializer.Deserialize<OutgoingWire>(json[start..(end + 1)], ParseOptions);
                }
                catch (JsonException)
                {
                    // fall through
                }
            }

            throw new LlmException("Model returned invalid JSON for the outgoing translation.", ex);
        }
    }

    private (ILlmBackend Service, LlmProvider Provider) Resolve()
    {
        var provider = config.Provider;
        return (backend is BackendSwitch sw ? sw.For(provider) : backend, provider);
    }

    private async Task<(string Ja, IReadOnlyList<Segment> Segments, string Back, OutgoingStyle Style)> RequestOutgoingAsync(
        ILlmBackend service, LlmProvider provider, string userContent, OutgoingStyle style, CancellationToken ct)
    {
        var request = BuildOutgoingRequest(provider, userContent);
        LlmTiming? timing = null;
        var content = await service.CompleteAsync(request, ct, t => timing = t).ConfigureAwait(false);
        LogTiming(provider, "outgoing", 1, request.Model, timing);
        return ParseOutgoing(content, style);
    }

    private void LogTiming(LlmProvider provider, string kind, int lineCount, string requestedModel, LlmTiming? t)
    {
        if (t is null)
        {
            return;
        }

        var name = provider == LlmProvider.Anthropic ? "Claude API" : "OpenRouter";
        var cached = t.CachedPromptTokens is { } c ? $" (cached {c})" : string.Empty;
        var written = t.CacheWriteTokens is { } w && w > 0 ? $" (cache write {w})" : string.Empty;
        log.Debug(
            $"{name} {kind}: {lineCount} line(s), requested {requestedModel}, served {t.Model ?? "?"} via {t.Provider ?? "?"}, " +
            $"first token {Ms(t.FirstTokenMs)}, total {t.TotalMs:0} ms, tokens in {t.PromptTokens?.ToString() ?? "?"}{cached}{written} " +
            $"out {t.CompletionTokens?.ToString() ?? "?"}, finish {t.FinishReason ?? "?"}");
        if (t.FinishReason is "length" or "max_tokens")
        {
            log.Warning($"{name} {kind} hit max_tokens; the translation may be cut off.");
        }
    }

    private IReadOnlyList<string> SnapshotFallbacks()
    {
        try
        {
            return config.FallbackModels.ToArray();
        }
        catch (Exception)
        {
            return []; // list resized concurrently by the config window; skip fallbacks for this one request
        }
    }

    private static string Ms(double? ms) => ms is { } v ? $"{v:0} ms" : "n/a";

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static string OneLine(string s) =>
        s.Contains('\n') || s.Contains('\r') ? s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ') : s;

    private static string StripCodeFence(string content)
    {
        var s = content.Trim();
        if (!s.StartsWith("```", StringComparison.Ordinal))
        {
            return s;
        }

        var firstNewline = s.IndexOf('\n');
        var lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline >= 0 && lastFence > firstNewline ? s[(firstNewline + 1)..lastFence].Trim() : s;
    }

    private sealed class OutgoingWire
    {
        [JsonPropertyName("ja")]
        public string? Ja { get; set; }

        [JsonPropertyName("segments")]
        public List<SegmentWire?>? Segments { get; set; }

        [JsonPropertyName("back")]
        public string? Back { get; set; }

        [JsonPropertyName("style")]
        public string? Style { get; set; }
    }

    private sealed class SegmentWire
    {
        [JsonPropertyName("ja")]
        public string? Ja { get; set; }

        [JsonPropertyName("reading")]
        public string? Reading { get; set; }

        [JsonPropertyName("en")]
        public string? En { get; set; }
    }
}
