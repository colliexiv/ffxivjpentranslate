using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Game.Text;
using JpEnChat.Models;

namespace JpEnChat.Translation;

/// <summary>Where the player is, read from the game on the framework thread (<c>GameContextProvider</c>).</summary>
/// <param name="Zone">Current zone name; empty when unknown.</param>
/// <param name="Duty">Current duty name while bound by duty; empty otherwise.</param>
/// <param name="Job">Current job abbreviation (e.g. <c>WHM</c>); empty when unknown.</param>
public sealed record GameEnvironment(string Zone, string Duty, string Job)
{
    public static readonly GameEnvironment Unknown = new(string.Empty, string.Empty, string.Empty);
}

/// <summary>One earlier chat line shown to the model as context.</summary>
/// <param name="Sender">Sender name, or <see cref="TranslationContext.Me"/> for the player's own lines.</param>
/// <param name="Original">The line as written.</param>
/// <param name="Translation">Its finished translation, or null when there is none.</param>
public sealed record ContextLine(string Sender, string Original, string? Translation);

/// <summary>
/// Game context sent with a translation request (PLAN §12): location, job, channel and the recent lines of the same
/// conversation. Built on the framework thread when a batch is queued, then read-only. Always sent in the user message,
/// never in the system prompt, so the cached system-prompt prefix stays byte-identical.
/// </summary>
public sealed record TranslationContext
{
    /// <summary>Sender label for the player's own lines.</summary>
    public const string Me = "Me";

    /// <summary>Each recent line is cut to this many characters.</summary>
    public const int MaxLineChars = 120;

    /// <summary>Upper bound of <see cref="Configuration.ContextLines"/>.</summary>
    public const int MaxContextLines = 15;

    public GameEnvironment Environment { get; init; } = GameEnvironment.Unknown;

    /// <summary>Channel name, e.g. "Party" or "Tell".</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>Recent lines of the same conversation, oldest first.</summary>
    public IReadOnlyList<ContextLine> RecentLines { get; init; } = [];

    /// <summary>
    /// Appends the context block:
    /// <c>Context (do not translate): zone=…; duty=…; my job=…; channel=…</c>, then <c>Recent lines:</c> and one
    /// <c>Sender: original → translation</c> line each (omitted when there are none). Ends with a newline.
    /// </summary>
    public void AppendTo(StringBuilder sb)
    {
        ArgumentNullException.ThrowIfNull(sb);
        sb.Append("Context (do not translate): zone=").Append(OrUnknown(Environment.Zone))
            .Append("; duty=").Append(Environment.Duty.Length == 0 ? "none" : Clean(Environment.Duty))
            .Append("; my job=").Append(OrUnknown(Environment.Job))
            .Append("; channel=").Append(OrUnknown(Channel))
            .Append('\n');
        if (RecentLines.Count == 0)
        {
            return;
        }

        sb.Append("Recent lines:\n");
        foreach (var line in RecentLines)
        {
            sb.Append(FormatLine(line)).Append('\n');
        }
    }

    /// <summary>The block as a string (see <see cref="AppendTo"/>).</summary>
    public string Format()
    {
        var sb = new StringBuilder();
        AppendTo(sb);
        return sb.ToString();
    }

    /// <summary><c>Sender: original → translation</c> (or without the arrow), one line, at most <see cref="MaxLineChars"/> characters.</summary>
    public static string FormatLine(ContextLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = Clean(line.Sender) + ": " + Clean(line.Original);
        if (!string.IsNullOrWhiteSpace(line.Translation))
        {
            text += " → " + Clean(line.Translation);
        }

        return text.Length <= MaxLineChars ? text : text[..(MaxLineChars - 1)] + "…";
    }

    private static string OrUnknown(string value) => value.Length == 0 ? "unknown" : Clean(value);

    private static string Clean(string value) => LlmTranslator.OneLine(value).Trim();
}

/// <summary>
/// Builds <see cref="TranslationContext"/>s from the chat log and the game environment. Call on the framework thread
/// (the environment delegate reads game state); the log access itself is thread-safe.
/// </summary>
public sealed class TranslationContextBuilder
{
    private readonly ChatLog chatLog;
    private readonly Func<int> contextLines;
    private readonly Func<GameEnvironment> environment;
    private readonly Func<XivChatType, string> channelName;

    /// <param name="chatLog">Source of recent lines.</param>
    /// <param name="contextLines">How many recent lines to include (clamped to 0–<see cref="TranslationContext.MaxContextLines"/>).</param>
    /// <param name="environment">Current zone, duty and job.</param>
    /// <param name="channelName">Display name of a channel.</param>
    public TranslationContextBuilder(ChatLog chatLog, Func<int> contextLines, Func<GameEnvironment> environment, Func<XivChatType, string> channelName)
    {
        this.chatLog = chatLog ?? throw new ArgumentNullException(nameof(chatLog));
        this.contextLines = contextLines ?? throw new ArgumentNullException(nameof(contextLines));
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
        this.channelName = channelName ?? throw new ArgumentNullException(nameof(channelName));
    }

    /// <summary>
    /// Context for an incoming batch whose first line is <paramref name="first"/>: lines received before it in the same
    /// channel (for tells: the same tell partner). A Party Finder listing gets no recent lines.
    /// </summary>
    public TranslationContext ForIncoming(ChatLine first)
    {
        ArgumentNullException.ThrowIfNull(first);
        if (first.IsPartyFinder)
        {
            return new TranslationContext { Environment = SafeEnvironment(), Channel = "Party Finder listing" };
        }

        var recent = Recent(chatLog.Snapshot(), first.Id, l => SameConversation(first.Kind, first.SenderName, first.SenderWorld, l), Count());
        return new TranslationContext { Environment = SafeEnvironment(), Channel = ChannelLabel(first.Kind), RecentLines = recent };
    }

    /// <summary>
    /// Context for an outgoing message to channel <paramref name="kind"/> (for tells: to <paramref name="tellTarget"/>,
    /// <c>Name Surname</c> or <c>Name Surname@World</c>; null or empty = any tell).
    /// </summary>
    public TranslationContext ForOutgoing(XivChatType kind, string? tellTarget)
    {
        var name = string.Empty;
        var world = string.Empty;
        if (!string.IsNullOrWhiteSpace(tellTarget))
        {
            var at = tellTarget.IndexOf('@');
            name = (at < 0 ? tellTarget : tellTarget[..at]).Trim();
            world = at < 0 ? string.Empty : tellTarget[(at + 1)..].Trim();
        }

        var recent = Recent(chatLog.Snapshot(), long.MaxValue, l => SameConversation(kind, name, world, l), Count());
        return new TranslationContext { Environment = SafeEnvironment(), Channel = ChannelLabel(kind), RecentLines = recent };
    }

    /// <summary>
    /// The last <paramref name="max"/> lines of <paramref name="snapshot"/> with an id below <paramref name="beforeId"/>
    /// that match <paramref name="sameConversation"/>, oldest first.
    /// </summary>
    public static IReadOnlyList<ContextLine> Recent(
        IReadOnlyList<ChatLine> snapshot, long beforeId, Func<ChatLine, bool> sameConversation, int max)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(sameConversation);
        if (max <= 0)
        {
            return [];
        }

        var found = new List<ContextLine>(max);
        for (var i = snapshot.Count - 1; i >= 0 && found.Count < max; i--)
        {
            var line = snapshot[i];
            if (line.Id >= beforeId || line.IsPartyFinder || line.Original.Trim().Length == 0 || !sameConversation(line))
            {
                continue;
            }

            found.Add(ToContextLine(line));
        }

        found.Reverse();
        return found;
    }

    /// <summary>
    /// Whether <paramref name="line"/> belongs to the conversation of channel <paramref name="kind"/>: the same channel
    /// (party and cross-world party count as one); for tells, a tell with the same partner (any tell when
    /// <paramref name="partnerName"/> is empty; the world is compared only when both are known).
    /// </summary>
    public static bool SameConversation(XivChatType kind, string partnerName, string partnerWorld, ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (IsTell(kind))
        {
            if (!IsTell(line.Kind))
            {
                return false;
            }

            if (partnerName.Length == 0)
            {
                return true;
            }

            return string.Equals(line.SenderName, partnerName, StringComparison.OrdinalIgnoreCase)
                && (partnerWorld.Length == 0 || line.SenderWorld.Length == 0
                    || string.Equals(line.SenderWorld, partnerWorld, StringComparison.OrdinalIgnoreCase));
        }

        return Group(line.Kind) == Group(kind);
    }

    /// <summary>A log row as a context line: own rows are "Me"; the translation only when finished.</summary>
    public static ContextLine ToContextLine(ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var sender = line.IsOwn || line.IsSentByPlugin ? TranslationContext.Me : line.SenderName;
        var translation = line.Status is TranslationStatus.Done or TranslationStatus.CacheHit or TranslationStatus.Corrected
            && line.Translation.Trim().Length > 0
            ? line.Translation
            : null;
        return new ContextLine(sender.Length == 0 ? "?" : sender, line.Original, translation);
    }

    private static bool IsTell(XivChatType kind) => kind is XivChatType.TellIncoming or XivChatType.TellOutgoing;

    private static XivChatType Group(XivChatType kind) => kind == XivChatType.CrossParty ? XivChatType.Party : kind;

    private int Count()
    {
        try
        {
            return Math.Clamp(contextLines(), 0, TranslationContext.MaxContextLines);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private string ChannelLabel(XivChatType kind) => IsTell(kind) ? "Tell" : channelName(kind);

    private GameEnvironment SafeEnvironment()
    {
        try
        {
            return environment() ?? GameEnvironment.Unknown;
        }
        catch (Exception)
        {
            return GameEnvironment.Unknown;
        }
    }
}
