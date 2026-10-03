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

/// <summary>The outgoing channel list, prefix building and parsing of a prefix typed into the input box.</summary>
internal static class OutgoingChannels
{
    /// <summary>Index of <c>Say</c> in <see cref="All"/>; the initial selection.</summary>
    public const int SayIndex = 0;

    /// <summary>Selectable channels, in combo order.</summary>
    public static readonly IReadOnlyList<OutgoingChannel> All = Build();

    // Typed command (lowercase, no trailing space) → index into All. Includes the long forms the game accepts.
    private static readonly Dictionary<string, int> Aliases = BuildAliases();

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
