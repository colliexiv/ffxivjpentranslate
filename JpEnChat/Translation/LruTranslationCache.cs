using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JpEnChat.Translation;

/// <summary>
/// Exact-match LRU translation cache (PLAN §3.2), persisted to <c>&lt;configDir&gt;/cache.json</c>.
/// </summary>
/// <remarks>
/// <para>Storage: <see cref="Dictionary{TKey,TValue}"/> of key → node in a <see cref="LinkedList{T}"/> whose
/// head is the most recently used entry. All state is guarded by one lock; every operation is O(1) apart from
/// <see cref="Save"/>, which snapshots under the lock and does file I/O outside it.</para>
/// <para>File format: a JSON array of <c>{"d":"ja-en"|"en-ja","k":normalizedKey,"v":translation}</c> ordered
/// from least to most recently used, so loading replays it with <see cref="Put"/> and reproduces the order.
/// Writes go to <c>cache.json.tmp</c> and are moved over <c>cache.json</c>, so a crash mid-write never leaves a
/// truncated cache. Keys are re-normalized on load (normalization is idempotent), which keeps old files valid
/// if the rules are tightened later.</para>
/// <para>Capacity is read from <c>maxEntries</c> on every <see cref="Put"/>, so a config change applies on the
/// next write. A capacity of 0 or less disables storing.</para>
/// </remarks>
public sealed class LruTranslationCache : ITranslationCache
{
    public const string FileName = "cache.json";

    private const string JaToEnTag = "ja-en";
    private const string EnToJaTag = "en-ja";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Japanese as UTF-8 instead of \uXXXX escapes (a third of the bytes). Not HTML-embedded, so this is safe.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object gate = new();
    private readonly object saveGate = new();
    private readonly Dictionary<CacheKey, LinkedListNode<Entry>> map = [];
    private readonly LinkedList<Entry> lru = new();
    private readonly string filePath;
    private readonly Func<int> maxEntries;
    private readonly ILog log;
    private bool dirty;

    public LruTranslationCache(string configDirectory, Func<int> maxEntries, ILog? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(configDirectory);
        ArgumentNullException.ThrowIfNull(maxEntries);
        filePath = Path.Combine(configDirectory, FileName);
        this.maxEntries = maxEntries;
        this.log = log ?? NullLog.Instance;
    }

    /// <summary>Full path of the persisted file.</summary>
    public string FilePath => filePath;

    public int Count
    {
        get
        {
            lock (gate)
            {
                return map.Count;
            }
        }
    }

    /// <summary>True when entries changed since the last successful load or save.</summary>
    public bool IsDirty
    {
        get
        {
            lock (gate)
            {
                return dirty;
            }
        }
    }

    public CacheKey CreateKey(string text, TranslationDirection direction) =>
        new(direction, TextNormalizer.Normalize(text, direction));

    public bool TryGet(CacheKey key, [NotNullWhen(true)] out string? translation)
    {
        lock (gate)
        {
            if (map.TryGetValue(key, out var node))
            {
                if (node != lru.First)
                {
                    lru.Remove(node);
                    lru.AddFirst(node);
                }

                translation = node.Value.Translation;
                return true;
            }
        }

        translation = null;
        return false;
    }

    public void Put(CacheKey key, string translation)
    {
        ArgumentNullException.ThrowIfNull(key.NormalizedText);
        ArgumentNullException.ThrowIfNull(translation);
        if (key.NormalizedText.Length == 0 || translation.Length == 0)
        {
            return;
        }

        var capacity = SafeCapacity();
        lock (gate)
        {
            if (map.TryGetValue(key, out var node))
            {
                if (node.Value.Translation == translation && node == lru.First)
                {
                    return;
                }

                node.Value = node.Value with { Translation = translation };
                lru.Remove(node);
                lru.AddFirst(node);
            }
            else if (capacity > 0)
            {
                map[key] = lru.AddFirst(new Entry(key, translation));
            }

            while (map.Count > Math.Max(capacity, 0) && lru.Last is { } last)
            {
                map.Remove(last.Value.Key);
                lru.RemoveLast();
            }

            dirty = true;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            if (map.Count == 0)
            {
                return;
            }

            map.Clear();
            lru.Clear();
            dirty = true;
        }
    }

    public void Load()
    {
        List<FileEntry>? entries;
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            using var stream = File.OpenRead(filePath);
            entries = JsonSerializer.Deserialize<List<FileEntry>>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            log.Warning($"Translation cache at {filePath} could not be read ({ex.GetType().Name}); starting empty.");
            return;
        }

        if (entries is null)
        {
            return;
        }

        var loaded = 0;
        foreach (var e in entries)
        {
            if (e?.K is not { Length: > 0 } k || e.V is not { Length: > 0 } v)
            {
                continue;
            }

            TranslationDirection direction;
            switch (e.D)
            {
                case JaToEnTag:
                    direction = TranslationDirection.JaToEn;
                    break;
                case EnToJaTag:
                    direction = TranslationDirection.EnToJa;
                    break;
                default:
                    continue;
            }

            Put(CreateKey(k, direction), v);
            loaded++;
        }

        lock (gate)
        {
            dirty = false;
        }

        log.Debug($"Translation cache loaded {loaded} entries ({Count} kept).");
    }

    public void Save() => SaveCore(onlyIfDirty: false);

    public void SaveIfDirty() => SaveCore(onlyIfDirty: true);

    private void SaveCore(bool onlyIfDirty)
    {
        // saveGate serializes writers so two saves never interleave on the temp file.
        lock (saveGate)
        {
            List<FileEntry> snapshot;
            lock (gate)
            {
                if (onlyIfDirty && !dirty)
                {
                    return;
                }

                snapshot = new List<FileEntry>(map.Count);
                for (var node = lru.Last; node is not null; node = node.Previous)
                {
                    var e = node.Value;
                    snapshot.Add(new FileEntry(Tag(e.Key.Direction), e.Key.NormalizedText, e.Translation));
                }

                dirty = false;
            }

            var tempPath = filePath + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, snapshot, JsonOptions);
                }

                File.Move(tempPath, filePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (gate)
                {
                    dirty = true;
                }

                log.Warning($"Translation cache could not be saved to {filePath} ({ex.GetType().Name}: {ex.Message}).");
            }
        }
    }

    private int SafeCapacity()
    {
        try
        {
            return maxEntries();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string Tag(TranslationDirection direction) =>
        direction == TranslationDirection.EnToJa ? EnToJaTag : JaToEnTag;

    private sealed record Entry(CacheKey Key, string Translation);

    private sealed record FileEntry(
        [property: JsonPropertyName("d")] string? D,
        [property: JsonPropertyName("k")] string? K,
        [property: JsonPropertyName("v")] string? V);
}
