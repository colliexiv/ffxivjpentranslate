using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using JpEnChat.Chat;

namespace JpEnChat.PartyFinder;

/// <summary>Turns the raw bytes of a Party Finder listing's description into the plain text shown and translated.</summary>
/// <remarks>
/// <para>Rules, in order:</para>
/// <list type="number">
/// <item>The game marks auto-translate phrases in descriptions with two icon payloads,
/// <c>02 12 02 37 03</c> before and <c>02 12 02 38 03</c> after the phrase. ChatTranslated maps them to the
/// auto-translate bracket glyphs U+E040/U+E041 (<c>SeIconChar.AutoTranslateOpen/Close</c>); those are private-use
/// glyphs that the log font cannot draw, so they become <c>《</c> and <c>》</c> here, the same brackets
/// <see cref="SeStringText"/> puts around auto-translate phrases in chat (and which the translation prompt keeps
/// unchanged).</item>
/// <item>Text that still contains a payload marker (0x02) is decoded with <see cref="SeString.Parse(byte[])"/> and
/// flattened by <see cref="SeStringText.Flatten"/> (links keep their visible name, icons and colors are dropped).</item>
/// <item>Private-use glyphs and control characters become spaces; whitespace runs collapse to one space; the result is
/// trimmed.</item>
/// </list>
/// </remarks>
public static class PartyFinderText
{
    /// <summary>Icon payload the game puts before an auto-translate phrase in a listing description.</summary>
    public static readonly byte[] AutoTranslateOpenSequence = [0x02, 0x12, 0x02, 0x37, 0x03];

    /// <summary>Icon payload the game puts after an auto-translate phrase in a listing description.</summary>
    public static readonly byte[] AutoTranslateCloseSequence = [0x02, 0x12, 0x02, 0x38, 0x03];

    private const byte PayloadStart = 0x02;

    private static readonly byte[] OpenReplacement = Encoding.UTF8.GetBytes(" 《");
    private static readonly byte[] CloseReplacement = Encoding.UTF8.GetBytes("》 ");

    /// <summary>Cleans a description already read as a .NET string (UTF-8 decoded; payload markers are ASCII and survive).</summary>
    public static string Clean(string raw, Func<AutoTranslatePayload, string>? resolveAutoTranslate = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return Clean(Encoding.UTF8.GetBytes(raw), resolveAutoTranslate);
    }

    /// <summary>Cleans the raw UTF-8/SeString bytes of a description. See the type remarks for the rules.</summary>
    /// <param name="raw">Description bytes, without the terminating NUL.</param>
    /// <param name="resolveAutoTranslate">
    /// Resolves an <see cref="AutoTranslatePayload"/>; defaults to the game data lookup (see <see cref="SeStringText.Flatten"/>).
    /// </param>
    public static string Clean(ReadOnlySpan<byte> raw, Func<AutoTranslatePayload, string>? resolveAutoTranslate = null)
    {
        var nul = raw.IndexOf((byte)0);
        if (nul >= 0)
        {
            raw = raw[..nul];
        }

        if (raw.IsEmpty)
        {
            return string.Empty;
        }

        var bytes = Replace(raw.ToArray(), AutoTranslateOpenSequence, OpenReplacement);
        bytes = Replace(bytes, AutoTranslateCloseSequence, CloseReplacement);

        string text;
        if (Array.IndexOf(bytes, PayloadStart) >= 0)
        {
            try
            {
                text = SeStringText.Flatten(SeString.Parse(bytes), resolveAutoTranslate);
            }
            catch (Exception)
            {
                // Malformed payload: fall back to the bytes as text; the control characters are removed below.
                text = Encoding.UTF8.GetString(bytes);
            }
        }
        else
        {
            text = Encoding.UTF8.GetString(bytes);
        }

        return SeStringText.CollapseWhitespace(ControlToSpace(SeStringText.StripPrivateUse(text)));
    }

    /// <summary>Replaces every occurrence of <paramref name="find"/> in <paramref name="source"/>.</summary>
    private static byte[] Replace(byte[] source, byte[] find, byte[] replacement)
    {
        var index = source.AsSpan().IndexOf(find);
        if (index < 0)
        {
            return source;
        }

        var result = new List<byte>(source.Length + 16);
        var start = 0;
        while (index >= 0)
        {
            result.AddRange(source.AsSpan(start, index));
            result.AddRange(replacement);
            start += index + find.Length;
            index = source.AsSpan(start).IndexOf(find);
        }

        result.AddRange(source.AsSpan(start));
        return [.. result];
    }

    private static string ControlToSpace(string text)
    {
        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                return string.Create(text.Length, text, static (span, src) =>
                {
                    for (var i = 0; i < src.Length; i++)
                    {
                        span[i] = char.IsControl(src[i]) ? ' ' : src[i];
                    }
                });
            }
        }

        return text;
    }
}
