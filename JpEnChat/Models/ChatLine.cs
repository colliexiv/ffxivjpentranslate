using System;
using System.Threading;
using Dalamud.Game.Text;

namespace JpEnChat.Models;

/// <summary>Language of a chat line, decided client-side by script detection (PLAN §3.2).</summary>
public enum Lang
{
    Unknown,
    Ja,
    En,
    Other,
}

/// <summary>Lifecycle of the right-hand (translation) cell of a <see cref="ChatLine"/>.</summary>
public enum TranslationStatus
{
    /// <summary>Not sent for translation (e.g. English line, channel disabled for translation).</summary>
    None,

    /// <summary>Queued or waiting on the debounce/first token; UI shows "…".</summary>
    Pending,

    /// <summary>Tokens are arriving; <see cref="ChatLine.Translation"/> grows.</summary>
    Streaming,

    /// <summary>Translation complete.</summary>
    Done,

    /// <summary>Request failed; see <see cref="ChatLine.Error"/>. UI offers a retry.</summary>
    Failed,

    /// <summary>Filled instantly from the translation cache (including a fixed, pinned translation).</summary>
    CacheHit,

    /// <summary>
    /// The player replaced the translation by hand (log → right-click → Edit translation). Drawn like
    /// <see cref="Done"/> with a pencil mark; the pipeline never overwrites it, because a late job only writes to
    /// <see cref="Pending"/> and <see cref="Streaming"/> lines.
    /// </summary>
    Corrected,
}

/// <summary>
/// One row of the two-pane log: the original message on the left, its translation on the right.
/// </summary>
/// <remarks>
/// <para>Threading contract: every mutation of a <see cref="ChatLine"/> happens on the framework thread.
/// The ingest path creates lines there (chat events arrive on it), and the translation pipeline marshals
/// results back with <c>IFramework.RunOnFrameworkThread</c> (or a queue drained on that thread) before
/// touching <see cref="Status"/>, <see cref="Translation"/> or <see cref="Error"/>. Background tasks must
/// never write to a line directly.</para>
/// <para>The UI reads lines on the draw thread. Every field written after creation is a single reference or
/// enum assignment (streaming appends build a new string and assign it), so reads are never torn; a reader at
/// worst sees the previous value for one frame. No locking is needed for display. Do not add fields that need
/// multi-step updates without revisiting this.</para>
/// </remarks>
public sealed class ChatLine
{
    private static long lastId;

    /// <summary>Allocates a process-unique, monotonically increasing line id.</summary>
    public static long NextId() => Interlocked.Increment(ref lastId);

    public long Id { get; init; } = NextId();

    /// <summary>Local time the line was received.</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    public XivChatType Kind { get; init; }

    public string SenderName { get; init; } = string.Empty;

    public string SenderWorld { get; init; } = string.Empty;

    /// <summary>Plain text of the message (payloads flattened; links as placeholders).</summary>
    public string Original { get; init; } = string.Empty;

    public Lang OriginalLang { get; init; } = Lang.Unknown;

    public TranslationStatus Status { get; set; } = TranslationStatus.None;

    /// <summary>Translation text; appended to while <see cref="Status"/> is <see cref="TranslationStatus.Streaming"/>.</summary>
    public string Translation { get; set; } = string.Empty;

    /// <summary>Short, user-facing error when <see cref="Status"/> is <see cref="TranslationStatus.Failed"/>.</summary>
    public string? Error { get; set; }

    /// <summary>The local player sent this line (sender matches <c>IPlayerState.CharacterName</c>).</summary>
    public bool IsOwn { get; init; }

    /// <summary>The line was produced by this plugin's outgoing flow (left: EN draft, right: JA sent).</summary>
    public bool IsSentByPlugin { get; init; }

    /// <summary>
    /// Where the line came from when it is not a chat message, e.g. <see cref="PartyFinderSource"/>. The log shows it
    /// as the tag in place of the channel tag, and <see cref="Kind"/> is then <see cref="XivChatType.None"/>.
    /// </summary>
    public string? SourceLabel { get; init; }

    /// <summary>Optional context shown before the sender in the log, e.g. a Party Finder listing's duty name.</summary>
    public string? Context { get; init; }

    /// <summary>The line is a Party Finder listing description (<see cref="SourceLabel"/> is <see cref="PartyFinderSource"/>).</summary>
    public bool IsPartyFinder => string.Equals(SourceLabel, PartyFinderSource, StringComparison.Ordinal);

    /// <summary><see cref="SourceLabel"/> of Party Finder listing descriptions.</summary>
    public const string PartyFinderSource = "PF";
}
