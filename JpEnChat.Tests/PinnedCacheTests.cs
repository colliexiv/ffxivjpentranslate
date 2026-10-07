using System.IO;
using System.Linq;
using System.Text.Json;
using JpEnChat.Translation;
using Xunit;

namespace JpEnChat.Tests;

public class PinnedCacheTests
{
    private const TranslationDirection JaEn = TranslationDirection.JaToEn;

    [Fact]
    public void PinnedEntriesSurviveEvictionAndDoNotCountTowardCapacity()
    {
        var cache = new LruTranslationCache(TestUtil.TempDir(), () => 2);
        cache.Pin("ノ", JaEn, "o/");
        cache.Put("一", JaEn, "one");
        cache.Put("二", JaEn, "two");
        cache.Put("三", JaEn, "three");

        Assert.Equal(3, cache.Count); // 2 unpinned + 1 pinned
        Assert.Equal(1, cache.PinnedCount);
        Assert.True(cache.TryGet("ノ", JaEn, out var wave));
        Assert.Equal("o/", wave);
        Assert.False(cache.TryGet("一", JaEn, out _));
        Assert.True(cache.TryGet("三", JaEn, out _));
    }

    [Fact]
    public void ZeroCapacityStillKeepsPinnedEntries()
    {
        var cache = new LruTranslationCache(TestUtil.TempDir(), () => 0);
        cache.Put("一", JaEn, "one");
        cache.Pin("ノ", JaEn, "o/");
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGetPinned("ノ", JaEn, out _));
    }

    [Fact]
    public void UnpinnedPutNeverOverwritesPinned()
    {
        var cache = new LruTranslationCache(TestUtil.TempDir(), () => 10);
        cache.Pin("ノ", JaEn, "o/");
        var version = cache.Version;

        cache.Put("ノ", JaEn, "/in");
        cache.Put(cache.CreateKey("ノ", JaEn), "/in", pinned: false);

        Assert.True(cache.TryGetEntry(cache.CreateKey("ノ", JaEn), out var entry));
        Assert.Equal("o/", entry.Translation);
        Assert.True(entry.Pinned);
        Assert.Equal(version, cache.Version);
    }

    [Fact]
    public void PinnedPutReplacesUnpinnedAndKeepsDisplay()
    {
        var cache = new LruTranslationCache(TestUtil.TempDir(), () => 1);
        cache.Put("おつ！", JaEn, "good work");
        cache.Put(cache.CreateKey("おつ！", JaEn), "gg", pinned: true, display: "  おつ！ ");

        Assert.True(cache.TryGetEntry(cache.CreateKey("おつ", JaEn), out var entry));
        Assert.Equal("gg", entry.Translation);
        Assert.True(entry.Pinned);
        Assert.Equal("おつ！", entry.Display);
        Assert.Equal("おつ！", entry.Original);
        Assert.Equal("おつ", entry.Key.NormalizedText);

        // The entry left the LRU, so a full LRU does not evict it.
        cache.Put("一", JaEn, "one");
        cache.Put("二", JaEn, "two");
        Assert.True(cache.TryGetPinned("おつ", JaEn, out _));
    }

    [Fact]
    public void DisplayEqualToKeyIsNotStored()
    {
        var cache = new LruTranslationCache(TestUtil.TempDir(), () => 10);
        cache.Pin("ノ", JaEn, "o/");
        Assert.True(cache.TryGetEntry(cache.CreateKey("ノ", JaEn), out var entry));
        Assert.Null(entry.Display);
        Assert.Equal("ノ", entry.Original);
    }

    [Fact]
    public void ClearKeepsPinnedAndClearAllRemovesEverything()
    {
        var cache = new LruTranslationCache(TestUtil.TempDir(), () => 10);
        cache.Pin("ノ", JaEn, "o/");
        cache.Put("一", JaEn, "one");

        cache.Clear();
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet("ノ", JaEn, out _));
        Assert.False(cache.TryGet("一", JaEn, out _));

        cache.ClearAll();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.PinnedCount);
    }

    [Fact]
    public void RemoveSetPinnedAndVersion()
    {
        var cache = new LruTranslationCache(TestUtil.TempDir(), () => 1);
        var v0 = cache.Version;
        cache.Put("一", JaEn, "one");
        var v1 = cache.Version;
        Assert.True(v1 > v0);

        Assert.True(cache.TryGet("一", JaEn, out _));
        Assert.Equal(v1, cache.Version); // lookups are not mutations

        var key = cache.CreateKey("一", JaEn);
        cache.SetPinned(key, true);
        Assert.True(cache.Version > v1);
        Assert.Equal(1, cache.PinnedCount);

        // Pinned, it no longer occupies the single LRU slot.
        cache.Put("二", JaEn, "two");
        Assert.Equal(2, cache.Count);

        // Unpinning puts it at the LRU front; capacity 1 evicts the older unpinned entry.
        var v2 = cache.Version;
        cache.SetPinned(key, false);
        Assert.True(cache.Version > v2);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet("一", JaEn, out _));
        Assert.False(cache.TryGet("二", JaEn, out _));

        var v3 = cache.Version;
        cache.SetPinned(cache.CreateKey("missing", JaEn), true);
        Assert.Equal(v3, cache.Version);

        Assert.True(cache.Remove(key));
        Assert.False(cache.Remove(key));
        Assert.True(cache.Version > v3);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void SnapshotListsPinnedFirstThenMostRecent()
    {
        var cache = new LruTranslationCache(TestUtil.TempDir(), () => 10);
        cache.Put("一", JaEn, "one");
        cache.Pin("ノ", JaEn, "o/");
        cache.Put("二", JaEn, "two");
        cache.Pin("乙", JaEn, "gg");

        var snapshot = cache.Snapshot();
        Assert.Equal(["ノ", "乙", "二", "一"], snapshot.Select(e => e.Key.NormalizedText));
        Assert.Equal([true, true, false, false], snapshot.Select(e => e.Pinned));
        Assert.NotSame(snapshot, cache.Snapshot());
    }

    [Fact]
    public void PersistenceRoundTripKeepsPinFlagAndDisplay()
    {
        var dir = TestUtil.TempDir();
        var cache = new LruTranslationCache(dir, () => 10);
        cache.Put("一", JaEn, "one");
        cache.Pin("おつ！", JaEn, "gg");
        cache.Put("二", JaEn, "two");
        cache.Save();

        using (var doc = JsonDocument.Parse(File.ReadAllText(cache.FilePath)))
        {
            var arr = doc.RootElement;
            Assert.Equal(3, arr.GetArrayLength());
            var pinned = arr[0]; // pinned entries are written first
            Assert.Equal("ja-en", pinned.GetProperty("d").GetString());
            Assert.Equal("おつ", pinned.GetProperty("k").GetString());
            Assert.Equal("gg", pinned.GetProperty("v").GetString());
            Assert.True(pinned.GetProperty("p").GetBoolean());
            Assert.Equal("おつ！", pinned.GetProperty("o").GetString());
            Assert.False(arr[1].TryGetProperty("p", out _));
            Assert.False(arr[1].TryGetProperty("o", out _));
            Assert.Equal("一", arr[1].GetProperty("k").GetString()); // then unpinned, oldest first
        }

        // Capacity 1 on reload: the pinned entry does not count, the newest unpinned one is kept.
        var reloaded = new LruTranslationCache(dir, () => 1);
        reloaded.Load();
        Assert.Equal(2, reloaded.Count);
        Assert.False(reloaded.IsDirty);
        Assert.True(reloaded.TryGetEntry(reloaded.CreateKey("おつ", JaEn), out var entry));
        Assert.True(entry.Pinned);
        Assert.Equal("おつ！", entry.Display);
        Assert.True(reloaded.TryGet("二", JaEn, out _));
        Assert.False(reloaded.TryGet("一", JaEn, out _));
    }

    [Fact]
    public void LoaderAcceptsFilesWithAndWithoutPinField()
    {
        var dir = TestUtil.TempDir();
        File.WriteAllText(
            Path.Combine(dir, "cache.json"),
            "[{\"d\":\"ja-en\",\"k\":\"一\",\"v\":\"one\"},{\"d\":\"ja-en\",\"k\":\"ノ\",\"v\":\"o/\",\"p\":true},{\"d\":\"en-ja\",\"k\":\"o/\",\"v\":\"ノ\",\"p\":false}]");
        var cache = new LruTranslationCache(dir, () => 10);
        cache.Load();
        Assert.Equal(3, cache.Count);
        Assert.Equal(1, cache.PinnedCount);
        Assert.True(cache.TryGetPinned("ノ", JaEn, out _));
        Assert.False(cache.TryGetPinned("一", JaEn, out _));
        Assert.False(cache.TryGetPinned("o/", TranslationDirection.EnToJa, out _));
    }

    [Fact]
    public void DefaultsAreAddedWithoutReplacingUserPins()
    {
        var cache = new LruTranslationCache(TestUtil.TempDir(), () => 10);
        cache.Pin("ノ", JaEn, "raised hand");
        cache.Put("おつ", JaEn, "good job"); // unpinned: a default replaces it

        var added = FixedTranslationDefaults.AddTo(cache);

        Assert.Equal(FixedTranslationDefaults.Entries.Count - 1, added);
        Assert.True(cache.TryGetPinned("ノ", JaEn, out var wave));
        Assert.Equal("raised hand", wave);
        Assert.True(cache.TryGetPinned("おつ", JaEn, out var otsu));
        Assert.Equal("gg", otsu);
        Assert.True(cache.TryGetPinned("88", JaEn, out _));
        Assert.Equal(0, FixedTranslationDefaults.AddTo(cache));
    }
}
