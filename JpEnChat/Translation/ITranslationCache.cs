using System;
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

/// <summary>
/// Exact-match translation cache with LRU eviction, persisted to <c>ConfigDirectory/cache.json</c>
/// (Phase 2A, PLAN §3.2). Each line of a batch is cached individually.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe: lookups happen on the framework thread at ingest, writes happen
/// after results are marshalled back, and <see cref="Save"/> may run on a background thread.
/// </remarks>
public interface ITranslationCache
{
    /// <summary>Number of entries currently held.</summary>
    int Count { get; }

    /// <summary>
    /// Normalizes <paramref name="text"/> (NFKC, trim, collapse whitespace, strip trailing
    /// <c>www</c>/<c>ｗ</c>/<c>！</c>/<c>。</c> runs, lowercase when the source is English) and pairs it with
    /// <paramref name="direction"/>.
    /// </summary>
    CacheKey CreateKey(string text, TranslationDirection direction);

    bool TryGet(CacheKey key, [NotNullWhen(true)] out string? translation);

    /// <summary>Adds or refreshes an entry, evicting the least recently used one past the configured limit.</summary>
    void Put(CacheKey key, string translation);

    void Clear();

    /// <summary>Loads persisted entries; a missing or corrupt file yields an empty cache.</summary>
    void Load();

    /// <summary>Writes entries to disk atomically (temp file + replace).</summary>
    void Save();
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
}
