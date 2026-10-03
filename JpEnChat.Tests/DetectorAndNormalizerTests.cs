using JpEnChat.Models;
using JpEnChat.Translation;
using Xunit;

namespace JpEnChat.Tests;

public class DetectorTests
{
    private readonly ScriptLanguageDetector detector = new();

    [Theory]
    [InlineData("よろしくお願いします", Lang.Ja)]
    [InlineData("カタカナ", Lang.Ja)]
    [InlineData("散開", Lang.Ja)]
    [InlineData("佐々木", Lang.Ja)]
    [InlineData("々", Lang.Ja)]
    [InlineData("ｱﾘｶﾞﾄ", Lang.Ja)] // halfwidth katakana
    [InlineData("㐀", Lang.Ja)] // CJK ext A
    [InlineData("gg 1ボス行きます", Lang.Ja)] // mixed → Ja
    [InlineData("LB3お願い!", Lang.Ja)]
    [InlineData("hello there", Lang.En)]
    [InlineData("Café", Lang.En)]
    [InlineData("ｗｗｗ", Lang.En)] // fullwidth latin
    [InlineData("gg wp :)", Lang.En)]
    [InlineData("123 !!!", Lang.Other)]
    [InlineData("😀👍", Lang.Other)] // emoji only
    [InlineData("♪☆", Lang.Other)]
    [InlineData("", Lang.Other)] // game private-use glyph
    [InlineData("", Lang.Unknown)]
    [InlineData("   ", Lang.Unknown)]
    public void Detects(string text, Lang expected) => Assert.Equal(expected, detector.Detect(text));

    [Fact]
    public void NullIsUnknown() => Assert.Equal(Lang.Unknown, detector.Detect(null!));
}

public class NormalizerTests
{
    private static string Ja(string s) => TextNormalizer.Normalize(s, TranslationDirection.JaToEn);

    private static string En(string s) => TextNormalizer.Normalize(s, TranslationDirection.EnToJa);

    [Fact]
    public void NfkcFoldsFullwidthAndHalfwidth()
    {
        Assert.Equal("LB3お願い", Ja("ＬＢ３お願い"));
        Assert.Equal("アリガト", Ja("ｱﾘｶﾞﾄ"));
    }

    [Theory]
    [InlineData("草ｗｗｗ", "草")]
    [InlineData("草www", "草")]
    [InlineData("すごい!ｗｗ", "すごい")]
    [InlineData("おつかれ～♪", "おつかれ")]
    [InlineData("よろしくお願いします！！", "よろしくお願いします")]
    [InlineData("了解。", "了解")]
    [InlineData("うーん…", "うーん")]
    [InlineData("おつ 〜", "おつ")]
    public void StripsTrailingDecoration(string input, string expected) => Assert.Equal(expected, Ja(input));

    [Theory]
    [InlineData("wow", "wow")]
    [InlineData("new", "new")]
    [InlineData("show!!", "show")]
    [InlineData("ok www", "ok")]
    public void EnglishWordsEndingInWAreKept(string input, string expected) => Assert.Equal(expected, En(input));

    [Fact]
    public void QuestionMarkIsSignificant()
    {
        Assert.NotEqual(Ja("行く?"), Ja("行く"));
        Assert.Equal(Ja("行く？"), Ja("行く?"));
    }

    [Fact]
    public void WholeMessageOfDecorationIsKept()
    {
        Assert.Equal("www", Ja("ｗｗｗ"));
        Assert.Equal("!!!", Ja("！！！"));
        Assert.NotEqual(Ja("www"), Ja("ww"));
    }

    [Fact]
    public void LowercasesOnlyForEnglishSource()
    {
        Assert.Equal(En("hello"), En("Hello"));
        Assert.Equal("hello", En("HELLO!"));
        Assert.Equal("Tanakaさん", Ja("Tanakaさん"));
        Assert.NotEqual(Ja("PTお願い"), Ja("ptお願い"));
    }

    [Fact]
    public void CollapsesWhitespace()
    {
        Assert.Equal("1ボス 行きます", Ja("  1ボス　　行きます \t"));
        Assert.Equal("let's go", En("let's    go"));
    }

    [Fact]
    public void DifferentMessagesDoNotCollide()
    {
        Assert.NotEqual(Ja("散開"), Ja("頭割り"));
        Assert.NotEqual(Ja("1ボス"), Ja("2ボス"));
        Assert.NotEqual(Ja("行きます"), Ja("行きません"));
        Assert.NotEqual(En("new"), En("ne"));
        Assert.NotEqual(En("left"), En("left?"));
    }

    [Theory]
    [InlineData("草ｗｗｗ")]
    [InlineData("  ＡＢＣ　　ｄｅｆ！！ ")]
    [InlineData("ｗｗｗ")]
    [InlineData("! www")]
    [InlineData("Hello World...")]
    public void IsIdempotent(string input)
    {
        foreach (var dir in new[] { TranslationDirection.JaToEn, TranslationDirection.EnToJa })
        {
            var once = TextNormalizer.Normalize(input, dir);
            Assert.Equal(once, TextNormalizer.Normalize(once, dir));
        }
    }
}
