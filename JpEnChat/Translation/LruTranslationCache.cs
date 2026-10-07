using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JpEnChat.Translation;

/// <summary>
/// Exact-match LRU translation cache (PLAN §3.2) with pinned fixed translations (PLAN §11), persisted to
/// <c>&lt;configDir&gt;/cache.json</c>.
/// </summary>
/// <remarks>
/// <para>Storage: <see cref="Dictionary{TKey,TValue}"/> of key → node. A node lives in one of two linked lists:
/// <c>lru</c> (unpinned, head = most recently used) or <c>pinned</c> (insertion order, never evicted). All state is
/// guarded by one lock; every operation is O(1) apart from <see cref="Save"/> and <see cref="Snapshot"/>, which copy
/// under the lock (and <see cref="Save"/> does its file I/O outside it).</para>
/// <para>File format: a JSON array of <c>{"d":"ja-en"|"en-ja","k":normalizedKey,"v":translation}</c>, plus
/// <c>"p":true</c> on pinned entries and <c>"o":displayText</c> when the source as typed differs from the key (both
/// omitted otherwise, and both optional on load). Pinned entries come first, then unpinned ones from least to most
/// recently used, so loading replays the file with <c>Put</c> and reproduces the order. Writes go to
/// <c>cache.json.tmp</c> and are moved over <c>cache.json</c>, so a crash mid-write never leaves a truncated cache.
/// Keys are re-normalized on load (normalization is idempotent), which keeps old files valid if the rules are
/// tightened later.</para>
/// <para>Capacity (unpinned entries only) is read from <c>maxEntries</c> on every write, so a config change applies
/// on the next write. A capacity of 0 or less disables storing unpinned entries; pinned ones are always kept.</para>
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
    private readonly LinkedList<Entry> pinned = new();
    private readonly string filePath;
    private readonly Func<int> maxEntries;
    private readonly ILog log;
    private bool dirty;
    private int version;

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

    /// <summary>Number of pinned (fixed) entries.</summary>
    public int PinnedCount
    {
        get
        {
            lock (gate)
            {
                return pinned.Count;
            }
        }
    }

    public int Version
    {
        get
        {
            lock (gate)
            {
                return version;
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
                if (!node.Value.Pinned && node != lru.First)
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

    public bool TryGetEntry(CacheKey key, out CacheEntry entry)
    {
        lock (gate)
        {
            if (map.TryGetValue(key, out var node))
            {
                entry = node.Value.ToPublic();
                return true;
            }
        }

        entry = default;
        return false;
    }

    public void Put(CacheKey key, string translation) => Put(key, translation, pinned: false);

    public void Put(CacheKey key, string translation, bool pinned, string? display = null)
    {
        ArgumentNullException.ThrowIfNull(key.NormalizedText);
        ArgumentNullException.ThrowIfNull(translation);
        if (key.NormalizedText.Length == 0 || translation.Length == 0)
        {
            return;
        }

        display = CleanDisplay(key, display);
        var capacity = SafeCapacity();
        lock (gate)
        {
            if (map.TryGetValue(key, out var node))
            {
                var e = node.Value;
                if (e.Pinned && !pinned)
                {
                    return; // a fixed translation is never replaced by a model or cache result
                }

                var newDisplay = display ?? e.Display;
                if (pinned)
                {
                    if (e.Pinned && e.Translation == translation && e.Display == newDisplay)
                    {
                        return;
                    }

                    node.Value = new Entry(key, translation, true, newDisplay);
                    if (!e.Pinned)
                    {
                        lru.Remove(node);
                        this.pinned.AddLast(node);
                    }
                }
                else
                {
                    if (e.Translation == translation && e.Display == newDisplay && node == lru.First)
                    {
                        return;
                    }

                    node.Value = new Entry(key, translation, false, newDisplay);
                    lru.Remove(node);
                    lru.AddFirst(node);
                }
            }
            else if (pinned)
            {
                map[key] = this.pinned.AddLast(new Entry(key, translation, true, display));
            }
            else if (capacity > 0)
            {
                map[key] = lru.AddFirst(new Entry(key, translation, false, display));
            }
            else
            {
                return;
            }

            EvictLocked(capacity);
            MarkChangedLocked();
        }
    }

    public bool Remove(CacheKey key)
    {
        lock (gate)
        {
            if (!map.Remove(key, out var node))
            {
                return false;
            }

            node.List!.Remove(node);
            MarkChangedLocked();
            return true;
        }
    }

    public void SetPinned(CacheKey key, bool pinned)
    {
        var capacity = SafeCapacity();
        lock (gate)
        {
            if (!map.TryGetValue(key, out var node) || node.Value.Pinned == pinned)
            {
                return;
            }

            node.List!.Remove(node);
            node.Value = node.Value with { Pinned = pinned };
            if (pinned)
            {
                this.pinned.AddLast(node);
            }
            else
            {
                lru.AddFirst(node);
                EvictLocked(capacity);
            }

            MarkChangedLocked();
        }
    }

    public IReadOnlyList<CacheEntry> Snapshot()
    {
        lock (gate)
        {
            var list = new List<CacheEntry>(map.Count);
            foreach (var e in pinned)
            {
                list.Add(e.ToPublic());
            }

            foreach (var e in lru)
            {
                list.Add(e.ToPublic());
            }

            return list;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            if (lru.Count == 0)
            {
                return;
            }

            foreach (var e in lru)
            {
                map.Remove(e.Key);
            }

            lru.Clear();
            MarkChangedLocked();
        }
    }

    public void ClearAll()
    {
        lock (gate)
        {
            if (map.Count == 0)
            {
                return;
            }

            map.Clear();
            lru.Clear();
            pinned.Clear();
            MarkChangedLocked();
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

            Put(CreateKey(k, direction), v, e.P == true, e.O);
            loaded++;
        }

        lock (gate)
        {
            dirty = false;
        }

        log.Debug($"Translation cache loaded {loaded} entries ({Count} kept, {PinnedCount} fixed).");
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
                foreach (var e in pinned)
                {
                    snapshot.Add(ToFile(e));
                }

                for (var node = lru.Last; node is not null; node = node.Previous)
                {
                    snapshot.Add(ToFile(node.Value));
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

    /// <summary>Drops least recently used unpinned entries past <paramref name="capacity"/>. Caller holds <see cref="gate"/>.</summary>
    private void EvictLocked(int capacity)
    {
        while (lru.Count > Math.Max(capacity, 0) && lru.Last is { } last)
        {
            map.Remove(last.Value.Key);
            lru.RemoveLast();
        }
    }

    private void MarkChangedLocked()
    {
        dirty = true;
        version++;
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

    /// <summary>The display text worth storing: trimmed, and null when empty or equal to the key.</summary>
    private static string? CleanDisplay(CacheKey key, string? display)
    {
        var d = display?.Trim();
        return string.IsNullOrEmpty(d) || string.Equals(d, key.NormalizedText, StringComparison.Ordinal) ? null : d;
    }

    private static FileEntry ToFile(Entry e) =>
        new(Tag(e.Key.Direction), e.Key.NormalizedText, e.Translation, e.Pinned ? true : null, e.Display);

    private static string Tag(TranslationDirection direction) =>
        direction == TranslationDirection.EnToJa ? EnToJaTag : JaToEnTag;

    private sealed record Entry(CacheKey Key, string Translation, bool Pinned, string? Display)
    {
        public CacheEntry ToPublic() => new(Key, Translation, Pinned, Display);
    }

    private sealed record FileEntry(
        [property: JsonPropertyName("d")] string? D,
        [property: JsonPropertyName("k")] string? K,
        [property: JsonPropertyName("v")] string? V,
        [property: JsonPropertyName("p")] bool? P = null,
        [property: JsonPropertyName("o")] string? O = null);
}
