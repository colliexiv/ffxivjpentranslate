using System;
using System.Collections.Generic;
using JpEnChat.Translation;

namespace JpEnChat.Models;

/// <summary>State of the outgoing EN→JA flow in the quick-translate popup (PLAN §4.1, §9).</summary>
public enum OutgoingState
{
    /// <summary>User is typing English. Enter → <see cref="Translating"/>.</summary>
    Editing,

    /// <summary>EN→JA request in flight. Esc cancels back to <see cref="Editing"/>.</summary>
    Translating,

    /// <summary>Breakdown shown in the popup. Enter → <see cref="Sending"/>, Esc → <see cref="Editing"/>.</summary>
    Confirming,

    /// <summary>Queued to the framework thread for <c>IChatSender.Send</c>.</summary>
    Sending,
}

/// <summary>How outgoing Japanese should sound (Settings → General, and the popup's style selector).</summary>
public enum OutgoingStyle
{
    /// <summary>丁寧語: friendly です/ます, for strangers in Party Finder and the Duty Finder.</summary>
    Polite,

    /// <summary>タメ口: relaxed speech among friends.</summary>
    Casual,

    /// <summary>Composed, calm and concise; polite-leaning but not stiff ("cool bishoujo" type).</summary>
    Cool,

    /// <summary>The player's own persona text (<see cref="Configuration.CustomStyleText"/>).</summary>
    Custom,
}

/// <summary>Wire names of <see cref="OutgoingStyle"/>; they match the structured-output schema's <c>style</c> enum.</summary>
public static class Styles
{
    public const string Polite = "polite";
    public const string Casual = "casual";
    public const string Cool = "cool";
    public const string Custom = "custom";

    /// <summary>Every style, in selector order.</summary>
    public static readonly OutgoingStyle[] All = [OutgoingStyle.Polite, OutgoingStyle.Casual, OutgoingStyle.Cool, OutgoingStyle.Custom];

    /// <summary>The schema value of <paramref name="style"/>.</summary>
    public static string ToWire(OutgoingStyle style) => style switch
    {
        OutgoingStyle.Casual => Casual,
        OutgoingStyle.Cool => Cool,
        OutgoingStyle.Custom => Custom,
        _ => Polite,
    };

    /// <summary>Parses a schema value; null when it is not one.</summary>
    public static OutgoingStyle? FromWire(string? value) => value switch
    {
        Polite => OutgoingStyle.Polite,
        Casual => OutgoingStyle.Casual,
        Cool => OutgoingStyle.Cool,
        Custom => OutgoingStyle.Custom,
        _ => null,
    };

    /// <summary>Label for buttons and settings.</summary>
    public static string Label(OutgoingStyle style) => style switch
    {
        OutgoingStyle.Casual => "Casual",
        OutgoingStyle.Cool => "Cool",
        OutgoingStyle.Custom => "Custom",
        _ => "Polite",
    };

    /// <summary>Unknown enum values (hand-edited config) become <see cref="OutgoingStyle.Polite"/>.</summary>
    public static OutgoingStyle Normalize(OutgoingStyle style) =>
        Enum.IsDefined(style) ? style : OutgoingStyle.Polite;
}

/// <summary>One gloss in the breakdown panel: a Japanese chunk, its kana reading and its English meaning.</summary>
public sealed record Segment(string Ja, string Reading, string En);

/// <summary>
/// Immutable snapshot of an outgoing message. The translator returns a new instance (via <c>with</c>)
/// filled with <see cref="JapaneseText"/>, <see cref="Segments"/>, <see cref="BackTranslation"/> and
/// <see cref="Style"/>.
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

    /// <summary>Requested/returned style.</summary>
    public OutgoingStyle Style { get; init; } = OutgoingStyle.Polite;

    /// <summary>
    /// Game context (zone, duty, job, channel, recent lines) captured on the framework thread when the translation
    /// was requested; null when unavailable. Sent in the user message, never in the system prompt.
    /// </summary>
    public TranslationContext? Context { get; init; }

    /// <summary>Full line handed to <c>IChatSender.Send</c>.</summary>
    public string ToChatCommand() => ChannelPrefix + JapaneseText;
}
