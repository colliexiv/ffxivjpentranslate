using System.Collections.Generic;

namespace JpEnChat.Models;

/// <summary>State of the bottom input block for the outgoing EN→JA flow (PLAN §4.1).</summary>
public enum OutgoingState
{
    /// <summary>User is typing English. Enter → <see cref="Translating"/>.</summary>
    Editing,

    /// <summary>EN→JA request in flight. Esc cancels back to <see cref="Editing"/>.</summary>
    Translating,

    /// <summary>Breakdown shown above the input. Enter → <see cref="Sending"/>, Esc → <see cref="Editing"/>.</summary>
    Confirming,

    /// <summary>Queued to the framework thread for <c>IChatSender.Send</c>.</summary>
    Sending,
}

/// <summary>Politeness register values; match the structured-output schema enum.</summary>
public static class Registers
{
    public const string Casual = "casual";
    public const string Polite = "polite";
}

/// <summary>One gloss in the breakdown panel: a Japanese chunk, its kana reading and its English meaning.</summary>
public sealed record Segment(string Ja, string Reading, string En);

/// <summary>
/// Immutable snapshot of an outgoing message. The translator returns a new instance (via <c>with</c>)
/// filled with <see cref="JapaneseText"/>, <see cref="Segments"/>, <see cref="BackTranslation"/> and
/// <see cref="Register"/>.
/// </summary>
public sealed record OutgoingDraft
{
    /// <summary>What the user typed (without the channel prefix).</summary>
    public string EnglishText { get; init; } = string.Empty;

    /// <summary>Chat command prefix including trailing space, e.g. <c>"/p "</c> or <c>"/t Name@World "</c>.</summary>
    public string ChannelPrefix { get; init; } = string.Empty;

    /// <summary>Japanese to send. User may edit it in place before sending.</summary>
    public string JapaneseText { get; init; } = string.Empty;

    public IReadOnlyList<Segment> Segments { get; init; } = [];

    /// <summary>Model's English back-translation of <see cref="JapaneseText"/>, for confirmation.</summary>
    public string BackTranslation { get; init; } = string.Empty;

    /// <summary>Requested/returned register; one of <see cref="Registers"/>.</summary>
    public string Register { get; init; } = Registers.Polite;

    /// <summary>Full line handed to <c>IChatSender.Send</c>.</summary>
    public string ToChatCommand() => ChannelPrefix + JapaneseText;
}
