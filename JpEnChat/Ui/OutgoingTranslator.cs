using System;
using System.Threading;
using System.Threading.Tasks;
using JpEnChat.Models;
using JpEnChat.Translation;

namespace JpEnChat.Ui;

/// <summary>
/// What the outgoing flow needs from the translation pipeline: one EN→JA structured request (PLAN §4.1).
/// </summary>
/// <remarks>
/// Called from a thread-pool thread (the session wraps the call in <c>Task.Run</c>). Implementations must not touch
/// ImGui or game state. Cancellation (Esc, a newer request, popup dispose) is signalled through <paramref name="ct"/>
/// in <see cref="TranslateOutgoingAsync"/>; throwing <see cref="OperationCanceledException"/> is expected then.
/// </remarks>
public interface IOutgoingTranslator
{
    /// <summary>
    /// Returns a copy of <paramref name="draft"/> with <see cref="OutgoingDraft.JapaneseText"/>,
    /// <see cref="OutgoingDraft.Segments"/>, <see cref="OutgoingDraft.BackTranslation"/> and
    /// <see cref="OutgoingDraft.Style"/> filled. Input fields: <see cref="OutgoingDraft.EnglishText"/>, the requested
    /// <see cref="OutgoingDraft.Style"/> and <see cref="OutgoingDraft.Context"/>. Any other exception is shown to the user as its message.
    /// </summary>
    Task<OutgoingDraft> TranslateOutgoingAsync(OutgoingDraft draft, CancellationToken ct);
}

/// <summary>
/// Routes outgoing requests through <see cref="TranslationPipeline.TranslateOutgoingAsync"/>, so unloading the plugin
/// (pipeline dispose) cancels a request that is still in flight, in addition to the session's own token.
/// </summary>
public sealed class PipelineOutgoingTranslator(TranslationPipeline pipeline) : IOutgoingTranslator
{
    private readonly TranslationPipeline pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));

    public Task<OutgoingDraft> TranslateOutgoingAsync(OutgoingDraft draft, CancellationToken ct) =>
        pipeline.TranslateOutgoingAsync(draft, ct);
}
