using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace JpEnChat.Translation;

/// <summary>Which LLM service translations go to (Settings → Translation).</summary>
public enum LlmProvider
{
    /// <summary>OpenRouter (<see cref="OpenRouterClient"/>): many models behind one key.</summary>
    OpenRouter,

    /// <summary>The Claude API directly (<see cref="AnthropicClient"/>); Max/Team monthly API credits apply here.</summary>
    Anthropic,
}

/// <summary>Reasoning depth values of <see cref="LlmRequest.Effort"/>.</summary>
public static class Efforts
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
}

/// <summary>A named JSON schema the answer must follow (structured output).</summary>
/// <param name="Name">Schema name (OpenRouter's <c>json_schema.name</c>; the Claude API does not use it).</param>
/// <param name="Schema">The JSON schema object itself.</param>
public sealed record LlmJsonSchema(string Name, JsonObject Schema);

/// <summary>
/// One provider-neutral LLM request: a system prompt and one user message. Each <see cref="ILlmBackend"/> maps it to
/// its own wire format.
/// </summary>
public sealed record LlmRequest
{
    /// <summary>Primary model id in the provider's own naming (<c>google/gemini-3.8-flash</c>, <c>claude-haiku-5-5</c>).</summary>
    public required string Model { get; init; }

    /// <summary>Models to try after <see cref="Model"/> (OpenRouter's <c>models</c> list). The Claude API ignores it.</summary>
    public IReadOnlyList<string> FallbackModels { get; init; } = [];

    /// <summary>The built-in system prompt. Byte-identical across requests so providers can cache it.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>
    /// Request-varying text appended after <see cref="SystemPrompt"/> (the player's glossary section, including its
    /// leading blank line), or empty. Kept separate so it can stay outside the cached prefix.
    /// </summary>
    public string SystemSuffix { get; init; } = string.Empty;

    public required string UserContent { get; init; }

    /// <summary>Sampling temperature. The Claude API never receives it (current Claude models reject non-default values).</summary>
    public double? Temperature { get; init; }

    public int? MaxTokens { get; init; }

    /// <summary>Reasoning depth: one of <see cref="Efforts"/>; null or empty omits it (provider default / no reasoning).</summary>
    public string? Effort { get; init; }

    /// <summary>Structured output schema, or null for free text.</summary>
    public LlmJsonSchema? JsonSchema { get; init; }

    /// <summary><see cref="SystemPrompt"/> followed by <see cref="SystemSuffix"/>: the whole system prompt as one string.</summary>
    public string FullSystemPrompt => SystemPrompt + SystemSuffix;
}

/// <summary>Timing and usage of one completed request, for the latency log (PLAN §3.4).</summary>
/// <param name="Model">Model that actually served the request (differs from the requested one after a fallback).</param>
/// <param name="Provider">Upstream provider name, when the service reports it.</param>
/// <param name="FirstTokenMs">Milliseconds from send to the first non-empty content delta; null if none arrived.</param>
/// <param name="TotalMs">Milliseconds from send to end of stream/body.</param>
/// <param name="PromptTokens">Input tokens (for the Claude API: uncached + cache reads + cache writes).</param>
/// <param name="CachedPromptTokens">Input tokens read from the prompt cache.</param>
/// <param name="CompletionTokens">Output tokens, including thinking.</param>
/// <param name="FinishReason">OpenRouter <c>finish_reason</c> or Claude <c>stop_reason</c>.</param>
/// <param name="CacheWriteTokens">Input tokens written to the prompt cache (Claude API only).</param>
public sealed record LlmTiming(
    string? Model,
    string? Provider,
    double? FirstTokenMs,
    double TotalMs,
    int? PromptTokens,
    int? CachedPromptTokens,
    int? CompletionTokens,
    string? FinishReason,
    int? CacheWriteTokens = null);

/// <summary>An LLM service: streaming and non-streaming completion of one <see cref="LlmRequest"/>.</summary>
/// <remarks>Implementations are thread-safe and called from thread-pool threads.</remarks>
public interface ILlmBackend
{
    /// <summary>Streams the answer's text as it arrives.</summary>
    /// <param name="onTiming">Invoked once after a successful stream with timing and usage.</param>
    /// <exception cref="LlmException">Service error, refusal or malformed stream.</exception>
    /// <exception cref="TimeoutException">No data for the configured timeout.</exception>
    IAsyncEnumerable<string> StreamAsync(LlmRequest req, CancellationToken ct, Action<LlmTiming>? onTiming);

    /// <summary>Non-streaming completion; returns the answer's text.</summary>
    Task<string> CompleteAsync(LlmRequest req, CancellationToken ct, Action<LlmTiming>? onTiming);
}

/// <summary>
/// Delegates every call to the backend of the currently configured <see cref="LlmProvider"/>, so switching provider in
/// the settings applies to the next request without recreating anything.
/// </summary>
public sealed class BackendSwitch : ILlmBackend
{
    private readonly Func<LlmProvider> provider;
    private readonly ILlmBackend openRouter;
    private readonly ILlmBackend anthropic;

    public BackendSwitch(Func<LlmProvider> provider, ILlmBackend openRouter, ILlmBackend anthropic)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.openRouter = openRouter ?? throw new ArgumentNullException(nameof(openRouter));
        this.anthropic = anthropic ?? throw new ArgumentNullException(nameof(anthropic));
    }

    /// <summary>The backend for <paramref name="p"/>.</summary>
    public ILlmBackend For(LlmProvider p) => p == LlmProvider.Anthropic ? anthropic : openRouter;

    public IAsyncEnumerable<string> StreamAsync(LlmRequest req, CancellationToken ct, Action<LlmTiming>? onTiming) =>
        For(provider()).StreamAsync(req, ct, onTiming);

    public Task<string> CompleteAsync(LlmRequest req, CancellationToken ct, Action<LlmTiming>? onTiming) =>
        For(provider()).CompleteAsync(req, ct, onTiming);
}
