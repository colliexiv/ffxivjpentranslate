using System;
using System.Linq;
using System.Text;
using JpEnChat.PartyFinder;
using Xunit;

namespace JpEnChat.Tests;

/// <remarks>
/// Only payloads that decode without game data are used (new line, icon, auto-translate with an injected resolver);
/// see <see cref="SeStringTextTests"/> for the constructors that block without a running Dalamud.
/// </remarks>
public class PartyFinderTextTests
{
    // The sequences as ChatTranslated writes them (Utf8String.ToString of the description).
    private const string Open = "\u0002\u0012\u00027\u0003";
    private const string Close = "\u0002\u0012\u00028\u0003";

    private static readonly byte[] NewLine = [0x02, 0x10, 0x01, 0x03];
    private static readonly byte[] OtherIcon = [0x02, 0x12, 0x02, 0x05, 0x03];
    private static readonly byte[] AutoTranslateBytes = [0x02, 0x2E, 0x03, 0x02, 0x66, 0x03];

    private static byte[] Bytes(params object[] parts) =>
        parts.SelectMany(p => p is string s ? Encoding.UTF8.GetBytes(s) : (byte[])p).ToArray();

    [Theory]
    [InlineData("零式消化 よろしくお願いします", "零式消化 よろしくお願いします")]
    [InlineData("LF2M healer, chill run", "LF2M healer, chill run")]
    [InlineData("", "")]
    public void PlainTextPassesThrough(string raw, string expected)
    {
        Assert.Equal(expected, PartyFinderText.Clean(raw));
    }

    [Fact]
    public void ConstantsMatchChatTranslatedSequences()
    {
        Assert.Equal(Encoding.UTF8.GetBytes(Open), PartyFinderText.AutoTranslateOpenSequence);
        Assert.Equal(Encoding.UTF8.GetBytes(Close), PartyFinderText.AutoTranslateCloseSequence);
    }

    [Fact]
    public void IconSequencesBecomeAutoTranslateBrackets()
    {
        Assert.Equal("《初見》 練習", PartyFinderText.Clean(Open + "初見" + Close + "練習"));
        Assert.Equal("練習 《クリア目的》 です", PartyFinderText.Clean("練習" + Open + "クリア目的" + Close + "です"));
        Assert.Equal("《a》 《b》", PartyFinderText.Clean(Open + "a" + Close + Open + "b" + Close));
    }

    [Fact]
    public void PrivateUseGlyphsBecomeSpaces()
    {
        Assert.Equal("DPS あと1", PartyFinderText.Clean("DPSあと1"));
        Assert.Equal("ok", PartyFinderText.Clean("ok"));
    }

    [Fact]
    public void WhitespaceAndControlCharactersCollapse()
    {
        Assert.Equal("a b c d", PartyFinderText.Clean("  a\n\n b\t c\u0001d  "));
        Assert.Equal("1 2", PartyFinderText.Clean("1　　2"));
    }

    [Fact]
    public void PayloadBytesAreDecoded()
    {
        var raw = Bytes("1ボス", NewLine, "2ボス", OtherIcon, "まで");
        Assert.Equal("1ボス 2ボスまで", PartyFinderText.Clean(raw));
    }

    [Fact]
    public void AutoTranslatePayloadIsResolved()
    {
        var raw = Bytes("今日は", AutoTranslateBytes, "です");
        Assert.Equal("今日は《よろしくお願いします！》です", PartyFinderText.Clean(raw, _ => "よろしくお願いします！"));
    }

    [Fact]
    public void IconSequencesAndPayloadsTogether()
    {
        var raw = Bytes(Open, "零式", Close, NewLine, "消化");
        Assert.Equal("《零式》 消化", PartyFinderText.Clean(raw));
    }

    [Fact]
    public void StopsAtNul()
    {
        Assert.Equal("募集", PartyFinderText.Clean(Bytes("募集", new byte[] { 0x00 }, "garbage")));
    }

    [Fact]
    public void StringAndByteOverloadsAgree()
    {
        var text = "練習" + Open + "初見" + Close + "\nです";
        Assert.Equal(PartyFinderText.Clean(Encoding.UTF8.GetBytes(text)), PartyFinderText.Clean(text));
    }
}
