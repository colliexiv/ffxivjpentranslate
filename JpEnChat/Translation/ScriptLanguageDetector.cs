using JpEnChat.Models;

namespace JpEnChat.Translation;

/// <summary>
/// Script-based language detection (PLAN §3.2). Pure, allocation-free, one pass over the string.
/// </summary>
/// <remarks>
/// Rules, in priority order:
/// <list type="number">
/// <item>Any Japanese-script character → <see cref="Lang.Ja"/>: Hiragana/Katakana U+3040–30FF (includes the
/// prolonged sound mark ー), CJK Ext A U+3400–4DBF, CJK Unified U+4E00–9FFF, CJK Compatibility U+F900–FAFF,
/// iteration mark 々 U+3005, halfwidth Katakana U+FF66–FF9F. A single kana anywhere wins, so mixed JA/EN
/// lines go to the translator.</item>
/// <item>Else any Latin letter (ASCII, Latin-1/Extended-A/B, fullwidth Ａ–Ｚ/ａ–ｚ) → <see cref="Lang.En"/>.
/// Fullwidth "ｗｗｗ" therefore counts as EN and is not sent for translation.</item>
/// <item>Else, if the text contains anything but whitespace (digits, punctuation, emoji, game icon glyphs in
/// the Private Use Area) → <see cref="Lang.Other"/>.</item>
/// <item>Null, empty or whitespace-only → <see cref="Lang.Unknown"/>.</item>
/// </list>
/// Chinese text is reported as <see cref="Lang.Ja"/>; that is acceptable for a JA↔EN tool.
/// </remarks>
public sealed class ScriptLanguageDetector : ILanguageDetector
{
    public Lang Detect(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Lang.Unknown;
        }

        var sawLatin = false;
        var sawContent = false;
        foreach (var c in text)
        {
            if (IsJapanese(c))
            {
                return Lang.Ja;
            }

            if (!sawLatin && IsLatinLetter(c))
            {
                sawLatin = true;
            }

            if (!sawContent && !char.IsWhiteSpace(c))
            {
                sawContent = true;
            }
        }

        if (sawLatin)
        {
            return Lang.En;
        }

        return sawContent ? Lang.Other : Lang.Unknown;
    }

    /// <summary>True for kana, kanji and the Japanese-specific marks listed in the class remarks.</summary>
    public static bool IsJapanese(char c) =>
        c is (>= '぀' and <= 'ヿ')   // Hiragana + Katakana (incl. ー, ・, ゛゜)
            or (>= '㐀' and <= '䶿') // CJK Unified Ideographs Extension A
            or (>= '一' and <= '鿿') // CJK Unified Ideographs
            or (>= '豈' and <= '﫿') // CJK Compatibility Ideographs
            or '々'                      // 々 ideographic iteration mark
            or (>= 'ｦ' and <= 'ﾟ'); // Halfwidth Katakana

    /// <summary>True for Latin letters (ASCII, Latin-1 Supplement, Extended-A/B, fullwidth).</summary>
    public static bool IsLatinLetter(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')
            or (>= 'Ａ' and <= 'Ｚ') or (>= 'ａ' and <= 'ｚ')
            || (c is >= 'À' and <= 'ɏ' && c != '×' && c != '÷');
}
