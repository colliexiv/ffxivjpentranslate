using System;
using System.Text;
using JpEnChat.Translation;

namespace JpEnChat.Chat;

/// <summary>
/// The checks <see cref="GameChatSender"/> makes before touching game memory. Pure, so it is unit-tested.
/// </summary>
public static class ChatSendValidation
{
    /// <summary>
    /// Returns the UTF-8 byte length of <paramref name="text"/> when it may be sent.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Empty or whitespace only, longer than <see cref="IChatSender.MaxMessageBytes"/> UTF-8 bytes, or contains a
    /// line break or other control character (the chat box is single-line; a newline would never come from typing).
    /// </exception>
    public static int Validate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Message is empty.", nameof(text));
        }

        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > IChatSender.MaxMessageBytes)
        {
            throw new ArgumentException(
                $"Message is {bytes} bytes; the game accepts at most {IChatSender.MaxMessageBytes}.",
                nameof(text));
        }

        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                throw new ArgumentException("Message contains a line break or control character.", nameof(text));
            }
        }

        return bytes;
    }

    /// <summary>
    /// The leading chat command (e.g. <c>/p</c>, <c>/t</c>, <c>/cwl1</c>) for logging, or <c>"(none)"</c>. Never includes
    /// the message body or a tell target.
    /// </summary>
    public static string ChannelPrefix(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var span = text.AsSpan().TrimStart();
        if (span.Length < 2 || span[0] != '/')
        {
            return "(none)";
        }

        var end = span.IndexOf(' ');
        var command = end < 0 ? span : span[..end];
        return command.Length > 16 ? "(long)" : command.ToString();
    }
}
