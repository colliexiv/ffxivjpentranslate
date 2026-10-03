using System;
using JpEnChat.Chat;
using JpEnChat.Translation;
using Xunit;

namespace JpEnChat.Tests;

public class ChatSendTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyIsRejected(string text)
    {
        Assert.Throws<ArgumentException>(() => ChatSendValidation.Validate(text));
    }

    [Fact]
    public void NullIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => ChatSendValidation.Validate(null!));
    }

    [Fact]
    public void ByteLimitIsInclusive()
    {
        Assert.Equal(IChatSender.MaxMessageBytes, ChatSendValidation.Validate(new string('a', IChatSender.MaxMessageBytes)));
        Assert.Throws<ArgumentException>(() => ChatSendValidation.Validate(new string('a', IChatSender.MaxMessageBytes + 1)));
    }

    [Fact]
    public void ByteLimitCountsUtf8NotChars()
    {
        // "/p " (3 bytes) + 165 kana (3 bytes each) = 498 bytes: fits. 167 kana = 504: too long.
        Assert.Equal(498, ChatSendValidation.Validate("/p " + new string('あ', 165)));
        Assert.Throws<ArgumentException>(() => ChatSendValidation.Validate("/p " + new string('あ', 167)));
    }

    [Theory]
    [InlineData("/p line one\nline two")]
    [InlineData("/p tab\there")]
    public void ControlCharactersAreRejected(string text)
    {
        Assert.Throws<ArgumentException>(() => ChatSendValidation.Validate(text));
    }

    [Theory]
    [InlineData("/p よろしく", "/p")]
    [InlineData("/t Tanaka Taro@Gaia secret", "/t")]
    [InlineData("  /cwl1 hi", "/cwl1")]
    [InlineData("hello", "(none)")]
    [InlineData("/", "(none)")]
    public void ChannelPrefixNeverIncludesTheBody(string text, string expected)
    {
        Assert.Equal(expected, ChatSendValidation.ChannelPrefix(text));
    }

    [Fact]
    public void RecentSendsConsumesMatchingTellEchoOnce()
    {
        var sends = new RecentSends();
        var now = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        sends.Add("/t Tanaka Taro@Gaia よろしくお願いします", now);

        Assert.False(sends.TryConsumeEcho("別のメッセージ", now));
        Assert.True(sends.TryConsumeEcho("よろしくお願いします", now.AddSeconds(1)));
        Assert.False(sends.TryConsumeEcho("よろしくお願いします", now.AddSeconds(2)));
    }

    [Fact]
    public void RecentSendsForgetsOldAndExcessEntries()
    {
        var sends = new RecentSends();
        var now = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        sends.Add("/t A@B old", now);
        Assert.False(sends.TryConsumeEcho("old", now + RecentSends.Window + TimeSpan.FromSeconds(1)));

        for (var i = 0; i < RecentSends.Capacity + 3; i++)
        {
            sends.Add($"/t A@B m{i}", now);
        }

        Assert.Equal(RecentSends.Capacity, sends.Count);
        Assert.False(sends.TryConsumeEcho("m0", now));
        Assert.True(sends.TryConsumeEcho($"m{RecentSends.Capacity + 2}", now));
    }
}
