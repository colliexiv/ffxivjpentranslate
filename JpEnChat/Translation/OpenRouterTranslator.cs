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
/// <see cref="ITranslator"/> over OpenRouter (PLAN §3.3, §4.1): streamed numbered-line batches for incoming chat,
/// strict JSON-schema output for the outgoing EN→JA flow.
/// </summary>
/// <remarks>
/// Reads <see cref="Configuration"/> on thread-pool threads. Only scalar properties and a defensive copy of
/// <see cref="Configuration.FallbackModels"/> are read, so a concurrent edit in the config window can at worst
/// apply one request late.
/// </remarks>
public sealed class OpenRouterTranslator : ITranslator
{
    /// <summary>Temperature for incoming batches.</summary>
    public const double IncomingTemperature = 0.2;

    /// <summary>Temperature for the outgoing structured request.</summary>
    public const double OutgoingTemperature = 0.3;

    private static readonly JsonSerializerOptions ParseOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Configuration config;
    private readonly OpenRouterClient client;
    private readonly ILog log;

    public OpenRouterTranslator(Configuration config, OpenRouterClient client, ILog log)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(log);
        this.config = config;
        this.client = client;
        this.log = log;
    }

    /// <summary>The <c>response_format</c> object for the outgoing request (PLAN §4.1).</summary>
    public static JsonObject OutgoingResponseFormat() => new()
    {
        ["type"] = "json_schema",
        ["json_schema"] = new JsonObject
        {
            ["name"] = "outgoing",
            ["strict"] = true,
            ["schema"] = new JsonObject
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
                    ["register"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray(Registers.Casual, Registers.Polite),
                    },
                },
                ["required"] = new JsonArray("ja", "segments", "back", "register"),
                ["additionalProperties"] = false,
            },
        },
    };

    /// <summary>Builds the numbered-lines user message. Newlines inside a message become spaces.</summary>
    public static string BuildBatchUserContent(IReadOnlyList<ChatLine> lines, Lang target)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var sb = new StringBuilder();
        if (target != Lang.En)
        {
            sb.Append("Target: ").Append(target == Lang.Ja ? "Japanese" : target.ToString()).Append('\n');
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
    /// Output budget. Generous on purpose: with reasoning models (Gemini 3.x thinks even at "low") reasoning
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

    public async Task TranslateBatchAsync(
        IReadOnlyList<ChatLine> lines,
        Lang target,
        IProgress<(long LineId, string Delta)> progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(progress);
        if (lines.Count == 0)
        {
            return;
        }

        var request = new ChatRequest
        {
            Model = config.Model,
            FallbackModels = SnapshotFallbacks(),
            SystemPrompt = Prompts.IncomingSystem,
            UserContent = BuildBatchUserContent(lines, target),
            Temperature = IncomingTemperature,
            MaxTokens = BatchMaxTokens(lines),
            ReasoningEffort = NullIfEmpty(config.ReasoningEffort),
            ExcludeReasoning = true,
            ProviderSort = "latency",
        };

        var router = new NumberedLineRouter(lines.Select(l => l.Id).ToArray(), progress);
        OpenRouterTiming? timing = null;
        await foreach (var delta in client.StreamChatAsync(request, ct, t => timing = t).ConfigureAwait(false))
        {
            router.Push(delta);
        }

        router.Complete();
        LogTiming("batch", lines.Count, request.Model, timing);

        var missing = router.MissingIds;
        if (missing.Count > 0)
        {
            throw new IncompleteBatchException(missing);
        }
    }

    public async Task<OutgoingDraft> TranslateOutgoingAsync(OutgoingDraft draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var register = draft.Register is Registers.Casual ? Registers.Casual : Registers.Polite;
        var english = OneLine(draft.EnglishText).Trim();
        if (english.Length == 0)
        {
            throw new ArgumentException("Nothing to translate.", nameof(draft));
        }

        var maxBytes = Math.Max(IChatSender.MaxMessageBytes - Encoding.UTF8.GetByteCount(draft.ChannelPrefix), 1);
        var userContent = $"Register: {register}\nText: {english}";

        var result = await RequestOutgoingAsync(userContent, register, ct).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetByteCount(result.Ja);
        if (bytes > maxBytes)
        {
            log.Debug($"Outgoing translation is {bytes} bytes (limit {maxBytes}); asking for a shorter version.");
            var shorter = userContent +
                $"\nThe previous Japanese was {bytes} UTF-8 bytes, but the chat limit is {maxBytes} bytes " +
                $"(about {maxBytes / 3} Japanese characters). Write a shorter version with the same meaning.";
            result = await RequestOutgoingAsync(shorter, register, ct).ConfigureAwait(false);
        }

        return draft with
        {
            JapaneseText = result.Ja,
            Segments = result.Segments,
            BackTranslation = result.Back,
            Register = result.Register,
        };
    }

    /// <summary>
    /// Parses the structured outgoing answer. Tolerates a surrounding code fence; falls back to the requested
    /// register when the model returns another value.
    /// </summary>
    /// <exception cref="OpenRouterException">Not valid JSON or <c>ja</c> is empty.</exception>
    public static (string Ja, IReadOnlyList<Segment> Segments, string Back, string Register) ParseOutgoing(
        string content, string requestedRegister)
    {
        ArgumentNullException.ThrowIfNull(content);
        var json = StripCodeFence(content);
        OutgoingWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<OutgoingWire>(json, ParseOptions);
        }
        catch (JsonException ex)
        {
            throw new OpenRouterException("Model returned invalid JSON for the outgoing translation.", ex);
        }

        var ja = wire?.Ja?.Trim();
        if (string.IsNullOrEmpty(ja))
        {
            throw new OpenRouterException(null, "Model returned an empty Japanese translation.");
        }

        var segments = (wire!.Segments ?? [])
            .Where(s => s is not null && !string.IsNullOrEmpty(s.Ja))
            .Select(s => new Segment(s!.Ja!, s.Reading ?? string.Empty, s.En ?? string.Empty))
            .ToArray();
        var register = wire.Register is Registers.Casual or Registers.Polite ? wire.Register : requestedRegister;
        return (ja, segments, wire.Back?.Trim() ?? string.Empty, register);
    }

    private async Task<(string Ja, IReadOnlyList<Segment> Segments, string Back, string Register)> RequestOutgoingAsync(
        string userContent, string register, CancellationToken ct)
    {
        var request = new ChatRequest
        {
            Model = config.OutgoingModel,
            SystemPrompt = Prompts.OutgoingSystem,
            UserContent = userContent,
            Temperature = OutgoingTemperature,
            MaxTokens = 4096,
            ReasoningEffort = NullIfEmpty(config.ReasoningEffort),
            ExcludeReasoning = true,
            ProviderSort = "latency",
            ResponseFormat = OutgoingResponseFormat(),
            RequireParameters = true,
        };

        OpenRouterTiming? timing = null;
        var content = await client.CompleteAsync(request, ct, t => timing = t).ConfigureAwait(false);
        LogTiming("outgoing", 1, request.Model, timing);
        return ParseOutgoing(content, register);
    }

    private void LogTiming(string kind, int lineCount, string requestedModel, OpenRouterTiming? t)
    {
        if (t is null)
        {
            return;
        }

        var cached = t.CachedPromptTokens is { } c ? $" (cached {c})" : string.Empty;
        log.Debug(
            $"OpenRouter {kind}: {lineCount} line(s), requested {requestedModel}, served {t.Model ?? "?"} via {t.Provider ?? "?"}, " +
            $"first token {Ms(t.FirstTokenMs)}, total {t.TotalMs:0} ms, tokens in {t.PromptTokens?.ToString() ?? "?"}{cached} " +
            $"out {t.CompletionTokens?.ToString() ?? "?"}, finish {t.FinishReason ?? "?"}");
        if (t.FinishReason == "length")
        {
            log.Warning($"OpenRouter {kind} hit max_tokens; the translation may be cut off.");
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

    private static string OneLine(string s) =>
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

        [JsonPropertyName("register")]
        public string? Register { get; set; }
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
