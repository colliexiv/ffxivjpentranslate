using System;
using System.Text;

namespace JpEnChat.Translation;

/// <summary>
/// Cache-key normalization (PLAN §3.2). Deliberately conservative: it only folds differences that do not
/// change what a translation should say.
/// </summary>
/// <remarks>
/// Rules, applied in this order:
/// <list type="number">
/// <item><b>NFKC.</b> Fullwidth ASCII → ASCII (ＡＢＣ１２３！ → ABC123!), ｗ → w, halfwidth katakana → fullwidth
/// (ｱﾘｶﾞﾄ → アリガト), ideographic space → space, … → "...", ‼ → "!!", ～ → ~.</item>
/// <item><b>Whitespace.</b> Trim, and collapse every internal whitespace run to one ASCII space.</item>
/// <item><b>Trailing decoration.</b> Strip a trailing run made of <c>! . 。 ~ 〜 ♪ ☆ ★ ♡ ・</c>, spaces, and
/// laughter <c>w</c>/<c>W</c>. A w-run is only stripped when the character before it is not an ASCII letter or
/// digit, so English words ending in w (<c>wow</c>, <c>new</c>, <c>show</c>) are never shortened, while
/// <c>草www</c>, <c>ok www</c> and <c>すごい!ｗｗ</c> are. <c>?</c> is kept: a question and a statement are
/// different messages. If stripping would leave nothing (<c>www</c>, <c>!!!</c>), nothing is stripped.</item>
/// <item><b>Case.</b> Lowercase (invariant) only for <see cref="TranslationDirection.EnToJa"/>; Japanese-source
/// keys keep case because embedded romaji such as party-finder codes or names can be case-significant.</item>
/// </list>
/// The function is idempotent: normalizing a normalized string returns it unchanged, so keys persisted by
/// <see cref="LruTranslationCache"/> can be re-normalized on load.
/// </remarks>
public static class TextNormalizer
{
    public static string Normalize(string text, TranslationDirection direction)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        var s = text.Normalize(NormalizationForm.FormKC);
        s = CollapseWhitespace(s);
        s = StripTrailingDecoration(s);
        if (direction == TranslationDirection.EnToJa)
        {
            s = s.ToLowerInvariant();
        }

        return s;
    }

    /// <summary>Characters stripped from the end of a message (after NFKC).</summary>
    public static bool IsTrailingDecoration(char c) =>
        c is '!' or '.' or '。' /* 。 */ or '~' or '〜' /* 〜 */ or '♪' /* ♪ */
            or '☆' /* ☆ */ or '★' /* ★ */ or '♡' /* ♡ */ or '・' /* ・ */;

    private static string CollapseWhitespace(string s)
    {
        // Fast path: no leading/trailing whitespace and no whitespace other than lone ASCII spaces.
        var needsWork = char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1]);
        for (var i = 0; !needsWork && i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c) && (c != ' ' || s[i + 1] == ' '))
            {
                needsWork = true;
            }
        }

        if (!needsWork)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        var pendingSpace = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string StripTrailingDecoration(string s)
    {
        var end = s.Length;
        while (end > 0)
        {
            var c = s[end - 1];
            if (c == ' ' || IsTrailingDecoration(c))
            {
                end--;
                continue;
            }

            if (c is 'w' or 'W')
            {
                var start = end - 1;
                while (start > 0 && s[start - 1] is 'w' or 'W')
                {
                    start--;
                }

                if (start == 0 || char.IsAsciiLetterOrDigit(s[start - 1]))
                {
                    break; // whole message is w's, or the w's end an English word ("wow")
                }

                end = start;
                continue;
            }

            break;
        }

        while (end > 0 && s[end - 1] == ' ')
        {
            end--;
        }

        return end == 0 || end == s.Length ? s : s[..end];
    }
}
