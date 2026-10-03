using System.Numerics;
using Dalamud.Game.Text;

namespace JpEnChat.Ui;

/// <summary>Display helpers for <see cref="XivChatType"/>: short tags, readable names and the game's default colors.</summary>
/// <remarks>All members return cached strings/values so they can be called per row per frame without allocating.</remarks>
internal static class ChatChannels
{
    // Game default chat colors (Character Configuration → Log Window → Log Colors, defaults).
    private static readonly Vector4 SayColor = Rgb(247, 247, 247);
    private static readonly Vector4 ShoutColor = Rgb(255, 166, 102);
    private static readonly Vector4 YellColor = Rgb(255, 255, 0);
    private static readonly Vector4 TellColor = Rgb(255, 184, 222);
    private static readonly Vector4 PartyColor = Rgb(102, 229, 255);
    private static readonly Vector4 AllianceColor = Rgb(255, 127, 0);
    private static readonly Vector4 FreeCompanyColor = Rgb(171, 219, 229);
    private static readonly Vector4 LinkshellColor = Rgb(212, 255, 125);
    private static readonly Vector4 NoviceNetworkColor = Rgb(212, 255, 125);
    private static readonly Vector4 EmoteColor = Rgb(186, 255, 240);
    private static readonly Vector4 OtherColor = Rgb(204, 204, 204);

    private static readonly string[] LsTags = ["[LS1]", "[LS2]", "[LS3]", "[LS4]", "[LS5]", "[LS6]", "[LS7]", "[LS8]"];

    private static readonly string[] CwlsTags =
        ["[CWLS1]", "[CWLS2]", "[CWLS3]", "[CWLS4]", "[CWLS5]", "[CWLS6]", "[CWLS7]", "[CWLS8]"];

    private static readonly string[] LsNames =
        ["Linkshell 1", "Linkshell 2", "Linkshell 3", "Linkshell 4", "Linkshell 5", "Linkshell 6", "Linkshell 7", "Linkshell 8"];

    private static readonly string[] CwlsNames =
    [
        "Cross-world LS 1", "Cross-world LS 2", "Cross-world LS 3", "Cross-world LS 4",
        "Cross-world LS 5", "Cross-world LS 6", "Cross-world LS 7", "Cross-world LS 8",
    ];

    /// <summary>Text color for rows produced by the plugin's own send path (PLAN §4: "distinct color").</summary>
    public static readonly Vector4 SentColor = Rgb(196, 160, 255);

    /// <summary>Short bracketed tag shown before the sender, e.g. <c>[P]</c>.</summary>
    public static string Tag(XivChatType kind) => kind switch
    {
        XivChatType.Say => "[S]",
        XivChatType.Shout => "[Sh]",
        XivChatType.Yell => "[Y]",
        XivChatType.Party => "[P]",
        XivChatType.CrossParty => "[P]",
        XivChatType.Alliance => "[A]",
        XivChatType.FreeCompany => "[FC]",
        XivChatType.TellIncoming => "[From]",
        XivChatType.TellOutgoing => "[To]",
        XivChatType.NoviceNetwork => "[NN]",
        XivChatType.PvPTeam => "[PvP]",
        XivChatType.CustomEmote or XivChatType.StandardEmote => "[Em]",
        XivChatType.Echo => "[Echo]",
        >= XivChatType.Ls1 and <= XivChatType.Ls8 => LsTags[kind - XivChatType.Ls1],
        XivChatType.CrossLinkShell1 => CwlsTags[0],
        >= XivChatType.CrossLinkShell2 and <= XivChatType.CrossLinkShell8 => CwlsTags[kind - XivChatType.CrossLinkShell2 + 1],
        _ => "[?]",
    };

    /// <summary>Human-readable channel name for settings and filter lists.</summary>
    public static string DisplayName(XivChatType kind) => kind switch
    {
        XivChatType.Say => "Say",
        XivChatType.Shout => "Shout",
        XivChatType.Yell => "Yell",
        XivChatType.Party => "Party",
        XivChatType.CrossParty => "Cross-world party",
        XivChatType.Alliance => "Alliance",
        XivChatType.FreeCompany => "Free Company",
        XivChatType.TellIncoming => "Tell (incoming)",
        XivChatType.TellOutgoing => "Tell (outgoing)",
        XivChatType.NoviceNetwork => "Novice Network",
        XivChatType.PvPTeam => "PvP team",
        XivChatType.CustomEmote => "Custom emote",
        XivChatType.StandardEmote => "Standard emote",
        XivChatType.Echo => "Echo",
        >= XivChatType.Ls1 and <= XivChatType.Ls8 => LsNames[kind - XivChatType.Ls1],
        XivChatType.CrossLinkShell1 => CwlsNames[0],
        >= XivChatType.CrossLinkShell2 and <= XivChatType.CrossLinkShell8 => CwlsNames[kind - XivChatType.CrossLinkShell2 + 1],
        _ => kind.ToString(),
    };

    /// <summary>The game's default log color for <paramref name="kind"/>.</summary>
    public static Vector4 Color(XivChatType kind) => kind switch
    {
        XivChatType.Say => SayColor,
        XivChatType.Shout => ShoutColor,
        XivChatType.Yell => YellColor,
        XivChatType.TellIncoming or XivChatType.TellOutgoing => TellColor,
        XivChatType.Party or XivChatType.CrossParty => PartyColor,
        XivChatType.Alliance => AllianceColor,
        XivChatType.FreeCompany or XivChatType.PvPTeam => FreeCompanyColor,
        XivChatType.NoviceNetwork => NoviceNetworkColor,
        XivChatType.CustomEmote or XivChatType.StandardEmote => EmoteColor,
        >= XivChatType.Ls1 and <= XivChatType.Ls8 => LinkshellColor,
        XivChatType.CrossLinkShell1 => LinkshellColor,
        >= XivChatType.CrossLinkShell2 and <= XivChatType.CrossLinkShell8 => LinkshellColor,
        _ => OtherColor,
    };

    private static Vector4 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);
}
