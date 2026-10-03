using System.IO;
using System.Text.Json;
using JpEnChat.Translation;
using Xunit;

namespace JpEnChat.Tests;

public class CacheTests
{
    [Fact]
    public void EvictsLeastRecentlyUsed()
    {
        var cache = new LruTranslationCache(TestUtil.TempDir(), () => 2);
        cache.Put("一", TranslationDirection.JaToEn, "one");
        cache.Put("二", TranslationDirection.JaToEn, "two");
        Assert.True(cache.TryGet("一", TranslationDirection.JaToEn, out _)); // touch 一 → 二 is now LRU
        cache.Put("三", TranslationDirection.JaToEn, "three");

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet("一", TranslationDirection.JaToEn, out var one));
        Assert.Equal("one", one);
        Assert.False(cache.TryGet("二", TranslationDirection.JaToEn, out _));
        Assert.True(cache.TryGet("三", TranslationDirection.JaToEn, out _));
    }

    [Fact]
    public void CapacityChangesApplyLive()
    {
        var max = 5;
        var cache = new LruTranslationCache(TestUtil.TempDir(), () => max);
        for (var i = 0; i < 5; i++)
        {
            cache.Put($"行{i}", TranslationDirection.JaToEn, $"line {i}");
        }

        max = 2;
        cache.Put("新", TranslationDirection.JaToEn, "new");
        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet("新", TranslationDirection.JaToEn, out _));
        Assert.True(cache.TryGet("行4", TranslationDirection.JaToEn, out _));
    }

    [Fact]
    public void DirectionIsPartOfKeyAndLookupIsNormalized()
    {
        var cache = new LruTranslationCache(TestUtil.TempDir(), () => 10);
        cache.Put("おつ", TranslationDirection.JaToEn, "gg");
        Assert.True(cache.TryGet("おつ！！ｗｗ", TranslationDirection.JaToEn, out var hit));
        Assert.Equal("gg", hit);
        Assert.False(cache.TryGet("おつ", TranslationDirection.EnToJa, out _));

        cache.Put("Hello!", TranslationDirection.EnToJa, "こんにちは");
        Assert.True(cache.TryGet("hello", TranslationDirection.EnToJa, out _));
    }

    [Fact]
    public void PersistenceRoundTripKeepsOrder()
    {
        var dir = TestUtil.TempDir();
        var cache = new LruTranslationCache(dir, () => 10);
        cache.Put("一", TranslationDirection.JaToEn, "one");
        cache.Put("二", TranslationDirection.JaToEn, "two");
        cache.Put("thanks", TranslationDirection.EnToJa, "ありがとう");
        Assert.True(cache.IsDirty);
        cache.SaveIfDirty();
        Assert.False(cache.IsDirty);
        Assert.False(File.Exists(Path.Combine(dir, "cache.json.tmp")));

        using (var doc = JsonDocument.Parse(File.ReadAllText(cache.FilePath)))
        {
            var arr = doc.RootElement;
            Assert.Equal(3, arr.GetArrayLength());
            Assert.Equal("ja-en", arr[0].GetProperty("d").GetString());
            Assert.Equal("一", arr[0].GetProperty("k").GetString()); // oldest first
            Assert.Equal("en-ja", arr[2].GetProperty("d").GetString());
            Assert.Equal("ありがとう", arr[2].GetProperty("v").GetString());
        }

        // Reload into a cache of 2: the oldest entry (一) is the one evicted, proving order survived.
        var reloaded = new LruTranslationCache(dir, () => 2);
        reloaded.Load();
        Assert.Equal(2, reloaded.Count);
        Assert.False(reloaded.IsDirty);
        Assert.False(reloaded.TryGet("一", TranslationDirection.JaToEn, out _));
        Assert.True(reloaded.TryGet("二", TranslationDirection.JaToEn, out var two));
        Assert.Equal("two", two);
        Assert.True(reloaded.TryGet("Thanks", TranslationDirection.EnToJa, out _));
    }

    [Fact]
    public void SaveIfDirtySkipsWhenClean()
    {
        var dir = TestUtil.TempDir();
        var cache = new LruTranslationCache(dir, () => 10);
        cache.SaveIfDirty();
        Assert.False(File.Exists(cache.FilePath));
        cache.Save();
        Assert.True(File.Exists(cache.FilePath));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"d\":\"ja-en\"}")]
    [InlineData("")]
    [InlineData("[{\"d\":\"xx\",\"k\":\"a\",\"v\":\"b\"},{\"d\":\"ja-en\",\"k\":null,\"v\":\"b\"},null,{\"d\":\"ja-en\",\"k\":\"散開\",\"v\":\"spread\"}]")]
    public void LoadToleratesCorruptFiles(string content)
    {
        var dir = TestUtil.TempDir();
        File.WriteAllText(Path.Combine(dir, "cache.json"), content);
        var log = new TestLog();
        var cache = new LruTranslationCache(dir, () => 10, log);
        cache.Load();
        Assert.True(cache.Count <= 1);
        if (cache.Count == 1)
        {
            Assert.True(cache.TryGet("散開", TranslationDirection.JaToEn, out var v));
            Assert.Equal("spread", v);
        }
    }

    [Fact]
    public void LoadToleratesMissingFile()
    {
        var cache = new LruTranslationCache(Path.Combine(TestUtil.TempDir(), "does-not-exist"), () => 10);
        cache.Load();
        Assert.Equal(0, cache.Count);
        cache.Put("一", TranslationDirection.JaToEn, "one");
        cache.Save(); // creates the directory
        Assert.True(File.Exists(cache.FilePath));
    }

    [Fact]
    public void ClearEmptiesAndMarksDirty()
    {
        var cache = new LruTranslationCache(TestUtil.TempDir(), () => 10);
        cache.Put("一", TranslationDirection.JaToEn, "one");
        cache.Save();
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.True(cache.IsDirty);
    }
}
