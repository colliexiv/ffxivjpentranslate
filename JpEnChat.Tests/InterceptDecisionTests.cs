using Dalamud.Game.Text;
using JpEnChat.Chat;
using Xunit;

namespace JpEnChat.Tests;

public class InterceptDecisionTests
{
    private static InterceptResult Decide(string text, bool enabled = true, string bypass = "\\", bool modifier = false) =>
        InterceptDecision.Decide(text, enabled, bypass, modifier);

    [Theory]
    [InlineData("よろしくお願いします")]
    [InlineData("1ボス行きます")]
    [InlineData("ok よろしく")]
    [InlineData("/p よろしく")]
    [InlineData("/dance")]
    [InlineData("/dance hello")]
    [InlineData("/xlplugins")]
    [InlineData("/e hello")]
    [InlineData("/echo hello")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/guide")]
    [InlineData("www.example.com")]
    [InlineData("12345")]
    [InlineData("1 2 3!")]
    [InlineData("/p 12345")]
    [InlineData("/p")]
    [InlineData("/p   ")]
    [InlineData("/t Tanaka Taro@Gaia")]
    [InlineData("/t")]
    [InlineData("♥♥")]
    public void PassesThroughEverythingThatIsNotPlainEnglishChat(string text)
    {
        Assert.Equal(InterceptAction.Pass, Decide(text).Action);
    }

    [Fact]
    public void PassesWhenDisabled()
    {
        Assert.Equal(InterceptAction.Pass, Decide("hello there", enabled: false).Action);
    }

    [Theory]
    [InlineData("hello there")]
    [InlineData("/p hello")]
    [InlineData("\\hello")]
    public void ModifierHeldPassesUnchanged(string text)
    {
        Assert.Equal(InterceptAction.Pass, Decide(text, modifier: true).Action);
    }

    [Fact]
    public void ModifierIsIgnoredWhenDisabled()
    {
        Assert.Equal(InterceptAction.Pass, Decide("hello", enabled: false, modifier: true).Action);
    }

    [Fact]
    public void ControlCharactersFromLinksPass()
    {
        // Item links and auto-translate phrases arrive as SeString macro bytes (0x02 ... 0x03).
        Assert.Equal(InterceptAction.Pass, Decide("check this \u0002'\u0003 out").Action);
    }

    [Theory]
    [InlineData("\\hello", "hello")]
    [InlineData("  \\hello there  ", "hello there")]
    [InlineData("\\ hello", "hello")]
    [InlineData("/p \\hello", "/p hello")]
    [InlineData("/t Tanaka Taro@Gaia \\see you", "/t Tanaka Taro@Gaia see you")]
    public void BypassPrefixIsStripped(string text, string expected)
    {
        var result = Decide(text);
        Assert.Equal(InterceptAction.PassRewritten, result.Action);
        Assert.Equal(expected, result.Rewritten);
    }

    [Fact]
    public void BypassPrefixAloneSendsUnchanged()
    {
        Assert.Equal(InterceptAction.Pass, Decide("\\").Action);
        Assert.Equal(InterceptAction.Pass, Decide("/p \\").Action);
    }

    [Fact]
    public void CustomBypassPrefix()
    {
        var result = Decide("!!gg", bypass: "!!");
        Assert.Equal(InterceptAction.PassRewritten, result.Action);
        Assert.Equal("gg", result.Rewritten);

        // With a different prefix configured, a backslash is just text.
        Assert.Equal(InterceptAction.Intercept, Decide("\\hello", bypass: "!!").Action);
    }

    [Fact]
    public void PlainEnglishIsIntercepted()
    {
        var result = Decide("hello");
        Assert.Equal(InterceptAction.Intercept, result.Action);
        Assert.Equal(string.Empty, result.ChannelPrefix);
        Assert.Equal("hello", result.Body);
        Assert.Null(result.ChannelKind);
    }

    [Fact]
    public void LeadingAndTrailingSpacesAreTrimmed()
    {
        var result = Decide("   good luck everyone   ");
        Assert.Equal(InterceptAction.Intercept, result.Action);
        Assert.Equal("good luck everyone", result.Body);

        var withPrefix = Decide("  /p  pull in 5  ");
        Assert.Equal(InterceptAction.Intercept, withPrefix.Action);
        Assert.Equal("/p  ", withPrefix.ChannelPrefix);
        Assert.Equal("pull in 5", withPrefix.Body);
    }

    [Theory]
    [InlineData("/p hello", "/p ", "hello", XivChatType.Party)]
    [InlineData("/party hello", "/party ", "hello", XivChatType.Party)]
    [InlineData("/s hi all", "/s ", "hi all", XivChatType.Say)]
    [InlineData("/sh wts", "/sh ", "wts", XivChatType.Shout)]
    [InlineData("/fc gn", "/fc ", "gn", XivChatType.FreeCompany)]
    [InlineData("/l3 hi", "/l3 ", "hi", XivChatType.Ls3)]
    [InlineData("/linkshell8 hi", "/linkshell8 ", "hi", XivChatType.Ls8)]
    [InlineData("/cwl1 hi", "/cwl1 ", "hi", XivChatType.CrossLinkShell1)]
    [InlineData("/cwl2 hi", "/cwl2 ", "hi", XivChatType.CrossLinkShell2)]
    [InlineData("/r thanks", "/r ", "thanks", XivChatType.TellOutgoing)]
    [InlineData("/reply thanks", "/reply ", "thanks", XivChatType.TellOutgoing)]
    [InlineData("/beginner how do I", "/beginner ", "how do I", XivChatType.NoviceNetwork)]
    [InlineData("/P hello", "/P ", "hello", XivChatType.Party)]
    public void ChannelPrefixIsKeptExactly(string text, string prefix, string body, XivChatType kind)
    {
        var result = Decide(text);
        Assert.Equal(InterceptAction.Intercept, result.Action);
        Assert.Equal(prefix, result.ChannelPrefix);
        Assert.Equal(body, result.Body);
        Assert.Equal(kind, result.ChannelKind);
    }

    [Theory]
    [InlineData("/t Tanaka Taro@Gaia hello", "/t Tanaka Taro@Gaia ", "Tanaka Taro@Gaia", "hello")]
    [InlineData("/tell Tanaka Taro@Gaia hello there", "/tell Tanaka Taro@Gaia ", "Tanaka Taro@Gaia", "hello there")]
    [InlineData("/t Tanaka Taro hello", "/t Tanaka Taro ", "Tanaka Taro", "hello")]
    [InlineData("/t <t> hello", "/t <t> ", "<t>", "hello")]
    [InlineData("/t Tanaka Taro@Gaia meet @ 9", "/t Tanaka Taro@Gaia ", "Tanaka Taro@Gaia", "meet @ 9")]
    public void TellTargetIsPreserved(string text, string prefix, string target, string body)
    {
        var result = Decide(text);
        Assert.Equal(InterceptAction.Intercept, result.Action);
        Assert.Equal(prefix, result.ChannelPrefix);
        Assert.Equal(target, result.TellTarget);
        Assert.Equal(body, result.Body);
        Assert.Equal(XivChatType.TellOutgoing, result.ChannelKind);
    }

    [Fact]
    public void ReplyHasNoTarget()
    {
        Assert.Equal(string.Empty, Decide("/r thanks").TellTarget);
    }

    [Theory]
    [InlineData("see https://example.com")]
    [InlineData("gg 123")]
    [InlineData("o/")]
    public void EnglishWithLinksOrNumbersIsStillEnglish(string text)
    {
        Assert.Equal(InterceptAction.Intercept, Decide(text).Action);
    }

    [Fact]
    public void ConfigurationOverloadUsesDefaults()
    {
        var configuration = new Configuration();
        Assert.True(configuration.InterceptVanillaChat);
        Assert.Equal("\\", configuration.BypassPrefix);
        Assert.Equal(BypassModifier.Ctrl, configuration.BypassModifier);

        Assert.Equal(InterceptAction.Intercept, InterceptDecision.Decide("hello", configuration, modifierHeld: false).Action);
        Assert.Equal(InterceptAction.Pass, InterceptDecision.Decide("hello", configuration, modifierHeld: true).Action);
        Assert.Equal(InterceptAction.PassRewritten, InterceptDecision.Decide("\\hello", configuration, modifierHeld: false).Action);

        configuration.InterceptVanillaChat = false;
        Assert.Equal(InterceptAction.Pass, InterceptDecision.Decide("hello", configuration, modifierHeld: false).Action);
    }

    [Theory]
    [InlineData(null, "\\")]
    [InlineData("", "\\")]
    [InlineData("   ", "\\")]
    [InlineData("abcd", "\\")]
    [InlineData("/x", "\\")]
    [InlineData(" !! ", "!!")]
    [InlineData("#", "#")]
    public void BypassPrefixIsNormalized(string? value, string expected)
    {
        Assert.Equal(expected, Configuration.NormalizeBypassPrefix(value));
    }

    [Theory]
    [InlineData(0, XivChatType.TellOutgoing)]
    [InlineData(17, XivChatType.TellOutgoing)]
    [InlineData(1, XivChatType.Say)]
    [InlineData(2, XivChatType.Party)]
    [InlineData(6, XivChatType.FreeCompany)]
    [InlineData(8, XivChatType.NoviceNetwork)]
    [InlineData(9, XivChatType.CrossLinkShell1)]
    [InlineData(10, XivChatType.CrossLinkShell2)]
    [InlineData(16, XivChatType.CrossLinkShell8)]
    [InlineData(19, XivChatType.Ls1)]
    [InlineData(26, XivChatType.Ls8)]
    public void ShellChatTypeMapsToChannel(int chatType, XivChatType expected)
    {
        Assert.Equal(expected, Ui.OutgoingChannels.FromShellChatType(chatType));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(27)]
    [InlineData(100)]
    public void UnknownShellChatTypeIsNull(int chatType)
    {
        Assert.Null(Ui.OutgoingChannels.FromShellChatType(chatType));
    }

    [Theory]
    [InlineData(0x1000, 0x1000, 0x1000, (int)ChatInputHookSource.Both)]
    [InlineData(0x1000, 0x2000, 0x2000, (int)ChatInputHookSource.CallSiteDiffers)]
    [InlineData(0x1000, 0, 0x1000, (int)ChatInputHookSource.ClientStructsOnly)]
    [InlineData(0, 0x2000, 0x2000, (int)ChatInputHookSource.CallSiteOnly)]
    [InlineData(0, 0, 0, (int)ChatInputHookSource.None)]
    public void ChatInputHookTargetPrefersCallSite(long clientStructs, long callSite, long expected, int source)
    {
        var (address, chosen) = ChatInputHookTarget.Choose((nint)clientStructs, (nint)callSite);
        Assert.Equal((nint)expected, address);
        Assert.Equal((ChatInputHookSource)source, chosen);
    }
}
