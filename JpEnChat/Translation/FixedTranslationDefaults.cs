using System;
using System.Collections.Generic;

namespace JpEnChat.Translation;

/// <summary>
/// The starter set of fixed (pinned) JA→EN translations (PLAN §11): short chat conventions a model tends to get wrong
/// on their own, such as a lone <c>ノ</c> (a raised hand, "o/").
/// </summary>
/// <remarks>
/// Added once on first run (no <c>cache.json</c> yet) and on demand from Settings → Translations → "Add defaults".
/// Only keys that do not already hold a fixed translation are added, so a user's own correction is never replaced.
/// </remarks>
public static class FixedTranslationDefaults
{
    /// <summary>Original (as typed) → English.</summary>
    public static IReadOnlyList<(string Original, string Translation)> Entries { get; } =
    [
        ("ノ", "o/"),
        ("ノシ", "o/ (bye)"),
        ("88", "bye bye"),
        ("おつ", "gg"),
        ("乙", "gg"),
        ("よろ", "hi, let's go"),
        ("おけ", "ok"),
        ("りょ", "roger"),
    ];

    /// <summary>Pins every default whose key is not pinned yet. Returns how many were added.</summary>
    public static int AddTo(ITranslationCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var added = 0;
        foreach (var (original, translation) in Entries)
        {
            var key = cache.CreateKey(original, TranslationDirection.JaToEn);
            if (cache.TryGetEntry(key, out var existing) && existing.Pinned)
            {
                continue;
            }

            cache.Put(key, translation, pinned: true, display: original);
            added++;
        }

        return added;
    }
}
