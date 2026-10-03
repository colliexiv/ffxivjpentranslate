using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JpEnChat.Models;

namespace JpEnChat.Translation;

/// <summary>
/// LLM-backed translator (Phase 2A: OpenRouter streaming chat completions, PLAN §3.3).
/// </summary>
/// <remarks>
/// Implementations run on thread-pool threads and must not touch <see cref="ChatLine"/> state or any
/// game/ImGui API. They read only the immutable fields of the lines passed in (<see cref="ChatLine.Id"/>,
/// <see cref="ChatLine.Original"/>, <see cref="ChatLine.SenderName"/>, ...) and report output through
/// <c>progress</c>; the caller marshals that onto the framework thread.
/// </remarks>
public interface ITranslator
{
    /// <summary>
    /// Translates a batch (one sender's debounced burst) as numbered lines in a single streaming request.
    /// </summary>
    /// <param name="lines">Lines to translate, in display order. Their translations map back by <see cref="ChatLine.Id"/>.</param>
    /// <param name="target">Target language (<see cref="Lang.En"/> for incoming JA).</param>
    /// <param name="progress">Receives text deltas per line as they stream. Invoked on a background thread.</param>
    /// <param name="ct">Cancelled on plugin unload or when the job is superseded.</param>
    /// <returns>Completes when the stream ends. Faults (HTTP 4xx/5xx, timeout, parse) propagate as exceptions.</returns>
    Task TranslateBatchAsync(
        IReadOnlyList<ChatLine> lines,
        Lang target,
        IProgress<(long LineId, string Delta)> progress,
        CancellationToken ct);

    /// <summary>
    /// EN→JA with structured output (ja, segments, back-translation, register; PLAN §4.1).
    /// Uses <see cref="OutgoingDraft.EnglishText"/> and <see cref="OutgoingDraft.Register"/> as input.
    /// </summary>
    /// <returns>A copy of <paramref name="draft"/> with the Japanese fields filled.</returns>
    Task<OutgoingDraft> TranslateOutgoingAsync(OutgoingDraft draft, CancellationToken ct);
}
