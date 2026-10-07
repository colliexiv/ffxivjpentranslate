using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace JpEnChat.Translation;

/// <summary>Direction of a translation; part of every cache key.</summary>
public enum TranslationDirection
{
    JaToEn,
    EnToJa,
}

/// <summary>
/// Cache key: direction plus normalized source text. Construct only via
/// <see cref="ITranslationCache.CreateKey"/> so normalization is applied consistently.
/// </summary>
public readonly record struct CacheKey(TranslationDirection Direction, string NormalizedText);

/// <summary>One cache entry as seen by the settings editor and the log's context menu.</summary>
/// <param name="Key">Direction and normalized source text.</param>
/// <param name="Translation">The stored translation.</param>
/// <param name="Pinned">
/// A fixed translation (a user correction or a hand-added rule): never evicted, not counted toward the capacity,
/// kept by <see cref="ITranslationCache.Clear"/>, and never overwritten by an unpinned <c>Put</c>.
/// </param>
/// <param name="Display">The source text as the user typed or saw it, when it was recorded; null otherwise.</param>
public readonly record struct CacheEntry(CacheKey Key, string Translation, bool Pinned, string? Display)
{
    public TranslationDirection Direction => Key.Direction;

    /// <summary><see cref="Display"/> when recorded, else the normalized key text.</summary>
    public string Original => string.IsNullOrEmpty(Display) ? Key.NormalizedText : Display;
}

/// <summary>
/// Exact-match translation cache with LRU eviction, persisted to <c>ConfigDirectory/cache.json</c>
/// (Phase 2A, PLAN §3.2). Each line of a batch is cached individually. Pinned entries (PLAN §11) live in the same
/// store but outside the LRU.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe: lookups happen on the framework thread at ingest, writes happen
/// after results are marshalled back, and <see cref="Save"/> may run on a background thread.
/// </remarks>
public interface ITranslationCache
{
    /// <summary>Number of entries currently held, pinned ones included.</summary>
    int Count { get; }

    /// <summary>Incremented on every change of content (add, update, remove, pin, clear, load); not on a lookup.</summary>
    int Version { get; }

    /// <summary>
    /// Normalizes <paramref name="text"/> (NFKC, trim, collapse whitespace, strip trailing
    /// <c>www</c>/<c>ｗ</c>/<c>！</c>/<c>。</c> runs, lowercase when the source is English) and pairs it with
    /// <paramref name="direction"/>.
    /// </summary>
    CacheKey CreateKey(string text, TranslationDirection direction);

    bool TryGet(CacheKey key, [NotNullWhen(true)] out string? translation);

    /// <summary>Looks up an entry without touching its LRU position.</summary>
    bool TryGetEntry(CacheKey key, out CacheEntry entry);

    /// <summary>
    /// Adds or refreshes an unpinned entry, evicting the least recently used unpinned one past the configured limit.
    /// Does nothing when the key holds a pinned entry (pinned wins).
    /// </summary>
    void Put(CacheKey key, string translation);

    /// <summary>
    /// Adds or replaces an entry. With <paramref name="pinned"/> the entry becomes (or stays) a fixed translation and
    /// <paramref name="display"/> (when given) records the source as typed. Without it this is <see cref="Put(CacheKey,string)"/>.
    /// </summary>
    void Put(CacheKey key, string translation, bool pinned, string? display = null);

    /// <summary>Removes an entry, pinned or not. Returns false when the key was not present.</summary>
    bool Remove(CacheKey key);

    /// <summary>Pins or unpins an existing entry; no-op when absent. Unpinning moves it to the front of the LRU.</summary>
    void SetPinned(CacheKey key, bool pinned);

    /// <summary>
    /// All entries: pinned ones in the order they were pinned, then unpinned ones from most to least recently used.
    /// Allocates a new list on every call; the settings editor caches it and refreshes on <see cref="Version"/>.
    /// </summary>
    IReadOnlyList<CacheEntry> Snapshot();

    /// <summary>Removes every unpinned entry. Fixed translations are kept.</summary>
    void Clear();

    /// <summary>Removes every entry, fixed translations included.</summary>
    void ClearAll();

    /// <summary>Loads persisted entries; a missing or corrupt file yields an empty cache.</summary>
    void Load();

    /// <summary>Writes entries to disk atomically (temp file + replace).</summary>
    void Save();

    /// <summary>
    /// Writes entries only if they changed since the last load/save. Called by the pipeline on a timer and on
    /// dispose, so implementations should make the clean case cheap. The default just calls <see cref="Save"/>.
    /// </summary>
    void SaveIfDirty() => Save();
}

/// <summary>Convenience overloads over <see cref="ITranslationCache"/>.</summary>
public static class TranslationCacheExtensions
{
    public static bool TryGet(
        this ITranslationCache cache,
        string text,
        TranslationDirection direction,
        [NotNullWhen(true)] out string? translation)
    {
        ArgumentNullException.ThrowIfNull(cache);
        return cache.TryGet(cache.CreateKey(text, direction), out translation);
    }

    public static void Put(this ITranslationCache cache, string text, TranslationDirection direction, string translation)
    {
        ArgumentNullException.ThrowIfNull(cache);
        cache.Put(cache.CreateKey(text, direction), translation);
    }

    /// <summary>Pins <paramref name="translation"/> for <paramref name="text"/>, recording the text as typed.</summary>
    public static void Pin(this ITranslationCache cache, string text, TranslationDirection direction, string translation)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(text);
        cache.Put(cache.CreateKey(text, direction), translation, pinned: true, display: text.Trim());
    }

    /// <summary>The pinned translation for <paramref name="text"/>, if there is one.</summary>
    public static bool TryGetPinned(
        this ITranslationCache cache,
        string text,
        TranslationDirection direction,
        [NotNullWhen(true)] out string? translation)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (cache.TryGetEntry(cache.CreateKey(text, direction), out var entry) && entry.Pinned)
        {
            translation = entry.Translation;
            return true;
        }

        translation = null;
        return false;
    }
}
