using System;
using System.Threading;
using System.Threading.Tasks;
using JpEnChat.Models;
using JpEnChat.Translation;

namespace JpEnChat.Ui;

/// <summary>
/// What the outgoing composer needs from the translation pipeline: one EN→JA structured request (PLAN §4.1).
/// </summary>
/// <remarks>
/// Called from a thread-pool thread (the composer wraps the call in <c>Task.Run</c>). Implementations must not touch
/// ImGui or game state. Cancellation (Esc, a newer request, window dispose) is signalled through <paramref name="ct"/>
/// in <see cref="TranslateOutgoingAsync"/>; throwing <see cref="OperationCanceledException"/> is expected then.
/// </remarks>
public interface IOutgoingTranslator
{
    /// <summary>
    /// Returns a copy of <paramref name="draft"/> with <see cref="OutgoingDraft.JapaneseText"/>,
    /// <see cref="OutgoingDraft.Segments"/>, <see cref="OutgoingDraft.BackTranslation"/> and
    /// <see cref="OutgoingDraft.Register"/> filled. Input fields: <see cref="OutgoingDraft.EnglishText"/> and the
    /// requested <see cref="OutgoingDraft.Register"/>. Any other exception is shown to the user as its message.
    /// </summary>
    Task<OutgoingDraft> TranslateOutgoingAsync(OutgoingDraft draft, CancellationToken ct);
}

/// <summary>Adapts the pipeline's <see cref="ITranslator"/> to <see cref="IOutgoingTranslator"/>.</summary>
public sealed class TranslatorOutgoingAdapter(ITranslator inner) : IOutgoingTranslator
{
    private readonly ITranslator inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public Task<OutgoingDraft> TranslateOutgoingAsync(OutgoingDraft draft, CancellationToken ct) =>
        inner.TranslateOutgoingAsync(draft, ct);
}

/// <summary>
/// Placeholder used until the translation pipeline is wired in <see cref="Plugin"/> (Phase 2A).
/// Every request fails with a clear message so the UI's error path can be exercised in-game.
/// </summary>
public sealed class UnwiredOutgoingTranslator : IOutgoingTranslator
{
    public Task<OutgoingDraft> TranslateOutgoingAsync(OutgoingDraft draft, CancellationToken ct) =>
        Task.FromException<OutgoingDraft>(new NotImplementedException("pipeline not wired"));
}
