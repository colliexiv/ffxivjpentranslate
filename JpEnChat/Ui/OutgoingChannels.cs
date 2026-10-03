using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Game.Text;

namespace JpEnChat.Ui;

/// <summary>A chat channel the outgoing composer can send to.</summary>
/// <param name="Label">Name shown in the channel combo.</param>
/// <param name="Command">Chat command without trailing space, e.g. <c>/p</c>.</param>
/// <param name="Kind">Channel used for the local echo row (colors, filter).</param>
internal sealed record OutgoingChannel(string Label, string Command, XivChatType Kind)
{
    public bool IsTell => Kind == XivChatType.TellOutgoing;
}

/// <summary>
/// A leading chat-channel command split off a line typed into the game's chat box
/// (<see cref="OutgoingChannels.TrySplitChatCommand"/>).
/// </summary>
/// <param name="Prefix">The command exactly as typed, up to the body, including the trailing whitespace
/// (e.g. <c>"/t Tanaka Taro@Gaia "</c>). Leading whitespace before the command is not included.</param>
/// <param name="Body">The message after the prefix, trimmed. May be empty.</param>
/// <param name="Kind">Channel of the command (<see cref="XivChatType.TellOutgoing"/> for tells and replies).</param>
/// <param name="TellTarget">For <c>/t</c>: the target as typed (<c>Name Surname@World</c>, <c>Name Surname</c> or a
/// placeholder such as <c>&lt;t&gt;</c>). Empty otherwise, including for <c>/r</c>.</param>
/// <param name="IsReply"><c>/r</c> or <c>/reply</c>: a tell to whoever last sent you one.</param>
internal readonly record struct ChatCommandSplit(string Prefix, string Body, XivChatType Kind, string TellTarget, bool IsReply);

/// <summary>The outgoing channel list, prefix building and parsing of a prefix typed into the input box.</summary>
internal static class OutgoingChannels
{
    /// <summary>Index of <c>Say</c> in <see cref="All"/>; the initial selection.</summary>
    public const int SayIndex = 0;

    /// <summary>Selectable channels, in combo order.</summary>
    public static readonly IReadOnlyList<OutgoingChannel> All = Build();

    // Typed command (lowercase, no trailing space) → index into All. Includes the long forms the game accepts.
    private static readonly Dictionary<string, int> Aliases = BuildAliases();

    // Chat-box channel commands that are not in the composer's combo (no fixed target, or rarely used).
    // The Novice Network command spellings are listed generously; an unknown one is only an error message in game.
    private static readonly Dictionary<string, XivChatType> ExtraChatCommands = new(StringComparer.Ordinal)
    {
        ["/r"] = XivChatType.TellOutgoing,
        ["/reply"] = XivChatType.TellOutgoing,
        ["/beginner"] = XivChatType.NoviceNetwork,
        ["/n"] = XivChatType.NoviceNetwork,
        ["/nn"] = XivChatType.NoviceNetwork,
        ["/novice"] = XivChatType.NoviceNetwork,
    };

    /// <summary>
    /// The full prefix including trailing space, e.g. <c>"/p "</c> or <c>"/t Name Surname@World "</c>.
    /// Returns <c>null</c> for a tell without a target.
    /// </summary>
    public static string? Prefix(OutgoingChannel channel, string tellTarget)
    {
        if (!channel.IsTell)
        {
            return channel.Command + " ";
        }

        var target = tellTarget.Trim();
        return target.Length == 0 ? null : $"{channel.Command} {target} ";
    }

    /// <summary>UTF-8 byte length of <see cref="Prefix"/> without building the string (used per frame).</summary>
    public static int PrefixByteCount(OutgoingChannel channel, string tellTarget)
    {
        var count = Encoding.UTF8.GetByteCount(channel.Command) + 1;
        if (channel.IsTell)
        {
            count += Encoding.UTF8.GetByteCount(tellTarget.AsSpan().Trim()) + 1;
        }

        return count;
    }

    /// <summary>
    /// Recognizes a leading channel command typed into the input, e.g. <c>"/p hello"</c>, <c>"/l3 hi"</c> or
    /// <c>"/t First Last@World hi"</c>.
    /// </summary>
    /// <param name="input">Raw input box text.</param>
    /// <param name="channelIndex">Index into <see cref="All"/> when recognized.</param>
    /// <param name="tellTarget">For tells, <c>Name@World</c> (may be empty if the user typed only <c>/t</c>).</param>
    /// <param name="body">The text after the prefix, trimmed.</param>
    /// <returns><c>false</c> when the text does not start with a known channel command; outputs are then unset.</returns>
    public static bool TryParsePrefix(string input, out int channelIndex, out string tellTarget, out string body)
    {
        channelIndex = -1;
        tellTarget = string.Empty;
        body = string.Empty;

        var text = input.AsSpan().TrimStart();
        if (text.Length < 2 || text[0] != '/')
        {
            return false;
        }

        var spaceAt = text.IndexOf(' ');
        var command = (spaceAt < 0 ? text : text[..spaceAt]).ToString().ToLowerInvariant();
        if (!Aliases.TryGetValue(command, out channelIndex))
        {
            channelIndex = -1;
            return false;
        }

        var rest = spaceAt < 0 ? ReadOnlySpan<char>.Empty : text[(spaceAt + 1)..].TrimStart();
        if (All[channelIndex].IsTell)
        {
            // "First Last@World message": the target ends at the first space after the '@'.
            var at = rest.IndexOf('@');
            if (at >= 0)
            {
                var end = rest[at..].IndexOf(' ');
                var targetEnd = end < 0 ? rest.Length : at + end;
                tellTarget = rest[..targetEnd].Trim().ToString();
                rest = rest[targetEnd..];
            }
        }

        body = rest.Trim().ToString();
        return true;
    }

    /// <summary>
    /// Splits a leading chat-channel command off a line typed into the game's chat box, keeping the prefix exactly as
    /// typed so it can be put back in front of the translation. Used by the vanilla chat intercept.
    /// </summary>
    /// <remarks>
    /// <para>Recognized: every command in <see cref="All"/> and its long form, except <c>/e</c>/<c>/echo</c>, plus
    /// <c>/r</c>, <c>/reply</c> and the Novice Network commands. Anything else starting with <c>/</c> (emotes,
    /// <c>/xlplugins</c>, ...) is not a chat-channel command and returns <c>false</c>.</para>
    /// <para>Tell targets: a placeholder token such as <c>&lt;t&gt;</c>, otherwise the next two words
    /// (<c>First Last</c> or <c>First Last@World</c>), because character names are always two words.</para>
    /// </remarks>
    /// <returns><c>false</c> when the line does not start with a recognized chat-channel command, or a tell has no
    /// target.</returns>
    public static bool TrySplitChatCommand(string input, out ChatCommandSplit split)
    {
        split = default;
        ArgumentNullException.ThrowIfNull(input);

        var start = SkipWhitespace(input, 0);
        if (start >= input.Length || input[start] != '/')
        {
            return false;
        }

        var commandEnd = NextWhitespace(input, start);
        var command = input[start..commandEnd].ToLowerInvariant();

        XivChatType kind;
        var isTell = false;
        var isReply = false;
        if (Aliases.TryGetValue(command, out var index))
        {
            var channel = All[index];
            if (channel.Kind == XivChatType.Echo)
            {
                return false; // Echo is only visible to you; never translated.
            }

            kind = channel.Kind;
            isTell = channel.IsTell;
        }
        else if (ExtraChatCommands.TryGetValue(command, out kind))
        {
            isReply = kind == XivChatType.TellOutgoing;
        }
        else
        {
            return false;
        }

        var position = SkipWhitespace(input, commandEnd);
        var target = string.Empty;
        if (isTell)
        {
            var firstEnd = NextWhitespace(input, position);
            if (firstEnd == position)
            {
                return false;
            }

            var first = input.AsSpan(position, firstEnd - position);
            int targetEnd;
            if (first.Length >= 3 && first[0] == '<' && first[^1] == '>')
            {
                targetEnd = firstEnd;
            }
            else
            {
                var secondStart = SkipWhitespace(input, firstEnd);
                var secondEnd = NextWhitespace(input, secondStart);
                if (secondEnd == secondStart)
                {
                    return false;
                }

                targetEnd = secondEnd;
            }

            target = input[position..targetEnd];
            position = SkipWhitespace(input, targetEnd);
        }

        split = new ChatCommandSplit(input[start..position], input[position..].Trim(), kind, target, isReply);
        return true;
    }

    /// <summary>
    /// The channel selected in the game's chat box, from <c>RaptureShellModule.ChatType</c> (numbering as in ChatTwo's
    /// <c>InputChannel</c>: 0/17/18 tell, 1 say, 2 party, 3 alliance, 4 yell, 5 shout, 6 FC, 7 PvP team, 8 novice,
    /// 9–16 CWLS1–8, 19–26 LS1–8). <c>null</c> for unknown values.
    /// </summary>
    public static XivChatType? FromShellChatType(int chatType) => chatType switch
    {
        0 or 17 or 18 => XivChatType.TellOutgoing,
        1 => XivChatType.Say,
        2 => XivChatType.Party,
        3 => XivChatType.Alliance,
        4 => XivChatType.Yell,
        5 => XivChatType.Shout,
        6 => XivChatType.FreeCompany,
        7 => XivChatType.PvPTeam,
        8 => XivChatType.NoviceNetwork,
        9 => XivChatType.CrossLinkShell1,
        >= 10 and <= 16 => (XivChatType)((int)XivChatType.CrossLinkShell2 + chatType - 10),
        >= 19 and <= 26 => (XivChatType)((int)XivChatType.Ls1 + chatType - 19),
        _ => null,
    };

    private static int SkipWhitespace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }

        return i;
    }

    private static int NextWhitespace(string s, int i)
    {
        while (i < s.Length && !char.IsWhiteSpace(s[i]))
        {
            i++;
        }

        return i;
    }

    private static List<OutgoingChannel> Build()
    {
        var list = new List<OutgoingChannel>
        {
            new("Say", "/s", XivChatType.Say),
            new("Shout", "/sh", XivChatType.Shout),
            new("Yell", "/y", XivChatType.Yell),
            new("Party", "/p", XivChatType.Party),
            new("Alliance", "/a", XivChatType.Alliance),
            new("Free Company", "/fc", XivChatType.FreeCompany),
        };

        for (var i = 0; i < 8; i++)
        {
            list.Add(new($"LS{i + 1}", $"/l{i + 1}", (XivChatType)((int)XivChatType.Ls1 + i)));
        }

        for (var i = 0; i < 8; i++)
        {
            var kind = i == 0 ? XivChatType.CrossLinkShell1 : (XivChatType)((int)XivChatType.CrossLinkShell2 + i - 1);
            list.Add(new($"CWLS{i + 1}", $"/cwl{i + 1}", kind));
        }

        list.Add(new("Tell", "/t", XivChatType.TellOutgoing));
        list.Add(new("Echo (test)", "/e", XivChatType.Echo));
        return list;
    }

    private static Dictionary<string, int> BuildAliases()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < All.Count; i++)
        {
            map[All[i].Command] = i;
        }

        void Alias(string alias, string command) => map[alias] = map[command];

        Alias("/say", "/s");
        Alias("/shout", "/sh");
        Alias("/yell", "/y");
        Alias("/party", "/p");
        Alias("/alliance", "/a");
        Alias("/freecompany", "/fc");
        Alias("/tell", "/t");
        Alias("/echo", "/e");
        for (var i = 1; i <= 8; i++)
        {
            Alias($"/linkshell{i}", $"/l{i}");
            Alias($"/cwlinkshell{i}", $"/cwl{i}");
        }

        return map;
    }
}
