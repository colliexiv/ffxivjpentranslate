using System;
using System.Linq;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using JpEnChat.Chat;
using Xunit;

namespace JpEnChat.Tests;

/// <remarks>
/// Without a running Dalamud, the <see cref="PlayerPayload"/>, <see cref="AutoTranslatePayload"/> and
/// <see cref="UIForegroundPayload"/> constructors and <see cref="AutoTranslatePayload.Text"/> block forever waiting for
/// the data service. So player links are tested through <see cref="SeStringText.Combine"/>, auto-translate payloads are
/// parsed from bytes and always resolved through an injected resolver, and no test calls those constructors.
/// </remarks>
public class SeStringTextTests
{
    // Encoded auto-translate payload: START, type 0x2E, length 3, group, key, END. Parsing needs no game data.
    private static readonly byte[] AutoTranslateBytes = [0x02, 0x2E, 0x03, 0x02, 0x66, 0x03];

    private static readonly Func<AutoTranslatePayload, string> NoAutoTranslate =
        _ => throw new InvalidOperationException("not expected in this test");

    private static string Flatten(params Payload[] payloads) => SeStringText.Flatten(new SeString(payloads), NoAutoTranslate);

    [Fact]
    public void TextPayloadsAreConcatenated()
    {
        Assert.Equal("よろしくお願いします！", Flatten(new TextPayload("よろしく"), new TextPayload("お願いします！")));
    }

    [Fact]
    public void EmptyMessageIsEmpty()
    {
        Assert.Equal(string.Empty, Flatten());
        Assert.Equal(string.Empty, Flatten(new TextPayload("   ")));
    }

    [Fact]
    public void PrivateUseGlyphsAreRemoved()
    {
        // Item-link arrow, party slot number and auto-translate brackets all live in U+E000–U+F8FF.
        Assert.Equal("ハイポーション 買った", Flatten(new TextPayload("ハイポーション買った")));
        Assert.Equal("ok", Flatten(new TextPayload("ok")));
    }

    [Fact]
    public void NewLineBecomesSpace()
    {
        Assert.Equal("1ボス 2ボス", Flatten(new TextPayload("1ボス"), new NewLinePayload(), new TextPayload("2ボス")));
    }

    [Fact]
    public void WhitespaceIsCollapsedAndTrimmed()
    {
        Assert.Equal("a b c", Flatten(new TextPayload("  a \t "), new TextPayload("  b　　c  ")));
    }

    [Fact]
    public void IconAndRawPayloadsAreSkipped()
    {
        var text = Flatten(
            new IconPayload(BitmapFontIcon.CrossWorld),
            new TextPayload("買った "),
            new TextPayload("ハイポーション"),
            RawPayload.LinkTerminator,
            new TextPayload("!"));
        Assert.Equal("買った ハイポーション!", text);
    }

    [Fact]
    public void AutoTranslateIsResolvedAndBracketed()
    {
        var parsed = SeString.Parse(AutoTranslateBytes);
        Assert.IsType<AutoTranslatePayload>(Assert.Single(parsed.Payloads));

        var message = new SeString(new TextPayload("今日は"), parsed.Payloads[0], new TextPayload("です"));
        var text = SeStringText.Flatten(message, _ => " よろしくお願いします！ ");
        Assert.Equal("今日は《よろしくお願いします！》です", text);
    }

    [Fact]
    public void UnresolvableAutoTranslateFallsBackToPlaceholder()
    {
        var message = new SeString(SeString.Parse(AutoTranslateBytes).Payloads[0], new TextPayload(" hi"));

        Assert.Equal(SeStringText.AutoTranslatePlaceholder + " hi", SeStringText.Flatten(message, NoAutoTranslate));
        Assert.Equal(SeStringText.AutoTranslatePlaceholder + " hi", SeStringText.Flatten(message, _ => ""));
    }

    [Fact]
    public void PlayerLinkNameAppearsOnce()
    {
        // As the game sends it: link payload, then the visible name (here with a cross-world glyph), then a terminator.
        var text = SeStringText.Combine(
        [
            new TextPiece(TextPieceKind.Text, "thanks "),
            new TextPiece(TextPieceKind.Player, "Tanaka Taro"),
            new TextPiece(TextPieceKind.Text, "Tanaka Taro"),
            new TextPiece(TextPieceKind.Text, "!"),
        ]);
        Assert.Equal("thanks Tanaka Taro!", text);
    }

    [Fact]
    public void PlayerLinkWithoutVisibleNameStillShowsName()
    {
        var text = SeStringText.Combine(
        [
            new TextPiece(TextPieceKind.Player, "Tanaka Taro"),
            new TextPiece(TextPieceKind.Text, " よろしく"),
        ]);
        Assert.Equal("Tanaka Taro よろしく", text);
    }

    [Fact]
    public void TextAfterPlayerIsKeptWhenItIsNotTheName()
    {
        var text = SeStringText.Combine(
        [
            new TextPiece(TextPieceKind.Player, "Tanaka Taro"),
            new TextPiece(TextPieceKind.NewLine, string.Empty),
            new TextPiece(TextPieceKind.Text, "Tanaka Taro"),
        ]);
        Assert.Equal("Tanaka Taro Tanaka Taro", text);
    }

    [Fact]
    public void ToPiecesMapsKnownPayloadsAndSkipsTheRest()
    {
        var pieces = SeStringText.ToPieces(
            [new TextPayload("a"), new NewLinePayload(), new IconPayload(BitmapFontIcon.CrossWorld), RawPayload.LinkTerminator],
            NoAutoTranslate).ToList();

        Assert.Equal(
            [new TextPiece(TextPieceKind.Text, "a"), new TextPiece(TextPieceKind.NewLine, string.Empty)],
            pieces);
    }

    [Theory]
    [InlineData("Tanaka Taro", "Tanaka Taro")]
    [InlineData(" Suzuki  Hanako ", "Suzuki Hanako")]
    [InlineData("Sato Jiro", "Sato Jiro")]
    [InlineData("", "")]
    public void CleanNameStripsIconsAndSpaces(string raw, string expected)
    {
        Assert.Equal(expected, SeStringText.CleanName(raw));
    }
}
