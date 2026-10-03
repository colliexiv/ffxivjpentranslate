using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JpEnChat.Models;
using JpEnChat.Translation;
using Xunit;

namespace JpEnChat.Tests;

public sealed class PipelineTests : IDisposable
{
    private readonly FakeTranslator translator = new();
    private readonly LruTranslationCache cache = new(TestUtil.TempDir(), () => 100);
    private readonly TestLog log = new();
    private readonly object frameworkLock = new();
    private readonly TranslationPipeline pipeline;

    public PipelineTests()
    {
        // "Framework thread": run inline, but serialized, like the real single framework thread.
        pipeline = new TranslationPipeline(
            new Configuration { DebounceMs = 150, MaxConcurrency = 2, CacheEnabled = true },
            new ScriptLanguageDetector(),
            cache,
            translator,
            action =>
            {
                lock (frameworkLock)
                {
                    action();
                }
            },
            log);
    }

    public void Dispose() => pipeline.Dispose();

    private TranslationPipeline NewPipeline(int debounceMs) => new(
        new Configuration { DebounceMs = debounceMs, MaxConcurrency = 2, CacheEnabled = true },
        new ScriptLanguageDetector(),
        cache,
        translator,
        action =>
        {
            lock (frameworkLock)
            {
                action();
            }
        },
        log);

    private void Enqueue(ChatLine line)
    {
        lock (frameworkLock)
        {
            pipeline.Enqueue(line);
        }
    }

    [Fact]
    public async Task SameSenderWithinDebounceIsOneBatch()
    {
        var a = TestUtil.JaLine("よろしくお願いします");
        var b = TestUtil.JaLine("初見です");
        Enqueue(a);
        Enqueue(b);
        Assert.Equal(TranslationStatus.Pending, a.Status);

        await TestUtil.WaitUntil(() => a.Status == TranslationStatus.Done && b.Status == TranslationStatus.Done);

        var batch = Assert.Single(translator.Batches);
        Assert.Equal([a.Id, b.Id], batch.Select(l => l.Id));
        Assert.Equal("EN(よろしくお願いします)", a.Translation);
        Assert.Equal("EN(初見です)", b.Translation);
        Assert.Contains(log.Messages, m => m.StartsWith("INF Translated 2 line(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DifferentSendersAreSeparateBatches()
    {
        var a = TestUtil.JaLine("散開", sender: "A");
        var b = TestUtil.JaLine("頭割り", sender: "B");
        var c = TestUtil.JaLine("頭割り", sender: "A", world: "Ifrit"); // same name, other world
        Enqueue(a);
        Enqueue(b);
        Enqueue(c);

        await TestUtil.WaitUntil(() => new[] { a, b, c }.All(l => l.Status == TranslationStatus.Done));
        Assert.Equal(3, translator.Batches.Count);
    }

    [Fact]
    public async Task LineAfterDebounceStartsNewBatch()
    {
        var a = TestUtil.JaLine("一");
        Enqueue(a);
        await TestUtil.WaitUntil(() => a.Status == TranslationStatus.Done);
        var b = TestUtil.JaLine("二");
        Enqueue(b);
        await TestUtil.WaitUntil(() => b.Status == TranslationStatus.Done);
        Assert.Equal(2, translator.Batches.Count);
    }

    [Fact]
    public async Task CacheHitSkipsTranslator()
    {
        var first = TestUtil.JaLine("おつ");
        Enqueue(first);
        await TestUtil.WaitUntil(() => first.Status == TranslationStatus.Done);
        var calls = translator.Batches.Count;

        var again = TestUtil.JaLine("おつ！ｗｗ");
        Enqueue(again);

        Assert.Equal(TranslationStatus.CacheHit, again.Status); // synchronous
        Assert.Equal("EN(おつ)", again.Translation);
        await Task.Delay(300);
        Assert.Equal(calls, translator.Batches.Count);
    }

    [Fact]
    public void NonJapaneseIsNotTranslated()
    {
        var en = new ChatLine { Original = "gg", OriginalLang = Lang.En, SenderName = "X" };
        var unknown = new ChatLine { Original = "hello", SenderName = "X" }; // detected here
        Enqueue(en);
        Enqueue(unknown);
        Assert.Equal(TranslationStatus.None, en.Status);
        Assert.Equal(TranslationStatus.None, unknown.Status);
        Assert.Equal(0, pipeline.PendingLineCount);
    }

    [Fact]
    public async Task StreamingStatusThenDone()
    {
        translator.Gate = new TaskCompletionSource();
        var a = TestUtil.JaLine("練習");
        Enqueue(a);

        await TestUtil.WaitUntil(() => a.Status == TranslationStatus.Streaming);
        Assert.Equal("EN(", a.Translation);
        translator.Gate.SetResult();
        await TestUtil.WaitUntil(() => a.Status == TranslationStatus.Done);
        Assert.Equal("EN(練習)", a.Translation);
    }

    [Fact]
    public async Task FailureSetsFailedWithMessageAndRetryWorks()
    {
        translator.Failure = new OpenRouterException(401, "HTTP 401: No auth credentials found");
        var a = TestUtil.JaLine("周回");
        Enqueue(a);
        await TestUtil.WaitUntil(() => a.Status == TranslationStatus.Failed);
        Assert.Equal("401 bad key", a.Error);
        Assert.Contains(log.Messages, m => m.StartsWith("WRN", StringComparison.Ordinal));

        translator.Failure = null;
        lock (frameworkLock)
        {
            pipeline.Retry(a);
            Assert.Equal(TranslationStatus.Pending, a.Status);
            Assert.Null(a.Error);
        }

        await TestUtil.WaitUntil(() => a.Status == TranslationStatus.Done);
        Assert.Equal("EN(周回)", a.Translation);
        Assert.Equal(2, translator.Batches.Count);
    }

    [Fact]
    public async Task IncompleteBatchFailsOnlyMissingLines()
    {
        translator.SkipText = "二";
        var a = TestUtil.JaLine("一");
        var b = TestUtil.JaLine("二");
        Enqueue(a);
        Enqueue(b);

        await TestUtil.WaitUntil(() => b.Status == TranslationStatus.Failed);
        await TestUtil.WaitUntil(() => a.Status == TranslationStatus.Done);
        Assert.Equal("no output", b.Error);
        Assert.False(cache.TryGet("二", TranslationDirection.JaToEn, out _));
        Assert.True(cache.TryGet("一", TranslationDirection.JaToEn, out _));
    }

    [Fact]
    public async Task TimeoutMapsToShortMessage()
    {
        translator.Failure = new TimeoutException("stalled");
        var a = TestUtil.JaLine("全滅");
        Enqueue(a);
        await TestUtil.WaitUntil(() => a.Status == TranslationStatus.Failed);
        Assert.Equal("timeout", a.Error);
    }

    [Fact]
    public void RetryIgnoresLinesThatAreNotFailed()
    {
        var a = TestUtil.JaLine("一");
        a.Status = TranslationStatus.Done;
        lock (frameworkLock)
        {
            pipeline.Retry(a);
        }

        Assert.Equal(TranslationStatus.Done, a.Status);
    }

    [Fact]
    public async Task DisposeCancelsAndSavesCache()
    {
        var a = TestUtil.JaLine("解散");
        Enqueue(a);
        await TestUtil.WaitUntil(() => a.Status == TranslationStatus.Done);
        Assert.True(cache.IsDirty);

        translator.Gate = new TaskCompletionSource();
        var b = TestUtil.JaLine("抜けます");
        Enqueue(b);
        await TestUtil.WaitUntil(() => b.Status == TranslationStatus.Streaming);

        pipeline.Dispose();
        Assert.False(cache.IsDirty);
        await TestUtil.WaitUntil(() => translator.Cancelled > 0);

        var c = TestUtil.JaLine("後");
        Enqueue(c); // ignored after dispose
        Assert.Equal(TranslationStatus.None, c.Status);
    }

    [Fact]
    public async Task EnqueueImmediateSkipsTheDebounce()
    {
        // A 10 s debounce: only a line that bypasses it can finish within the wait below.
        using var slow = NewPipeline(debounceMs: 10_000);
        var batched = TestUtil.JaLine("一");
        var immediate = TestUtil.JaLine("二"); // same sender: must not join the pending batch
        lock (frameworkLock)
        {
            slow.Enqueue(batched);
            slow.EnqueueImmediate(immediate);
            Assert.Equal(TranslationStatus.Pending, immediate.Status);
        }

        await TestUtil.WaitUntil(() => immediate.Status == TranslationStatus.Done, timeoutMs: 3000);
        Assert.Equal("EN(二)", immediate.Translation);
        Assert.Equal(TranslationStatus.Pending, batched.Status);
        Assert.Equal(1, slow.PendingLineCount);
        var batch = Assert.Single(translator.Batches);
        Assert.Equal([immediate.Id], batch.Select(l => l.Id));
        Assert.True(cache.TryGet("二", TranslationDirection.JaToEn, out _));
    }

    [Fact]
    public async Task EnqueueImmediateUsesCacheAndLanguageGate()
    {
        var first = TestUtil.JaLine("募集");
        lock (frameworkLock)
        {
            pipeline.EnqueueImmediate(first);
        }

        await TestUtil.WaitUntil(() => first.Status == TranslationStatus.Done);
        var calls = translator.Batches.Count;

        var again = TestUtil.JaLine("募集", sender: "Other");
        var english = new ChatLine { Original = "LF healer, chill run", SenderName = "X" };
        lock (frameworkLock)
        {
            pipeline.EnqueueImmediate(again);
            pipeline.EnqueueImmediate(english);
        }

        Assert.Equal(TranslationStatus.CacheHit, again.Status); // synchronous
        Assert.Equal("EN(募集)", again.Translation);
        Assert.Equal(TranslationStatus.None, english.Status);
        await Task.Delay(200);
        Assert.Equal(calls, translator.Batches.Count);
    }

    [Fact]
    public async Task OutgoingPassesThrough()
    {
        var result = await pipeline.TranslateOutgoingAsync(new OutgoingDraft { EnglishText = "hi" }, CancellationToken.None);
        Assert.Equal("JA(hi)", result.JapaneseText);
    }

    /// <summary>Echo translator: streams "EN(" + text + ")" in two deltas per line.</summary>
    private sealed class FakeTranslator : ITranslator
    {
        public ConcurrentQueue<IReadOnlyList<ChatLine>> BatchQueue { get; } = new();

        public List<IReadOnlyList<ChatLine>> Batches => [.. BatchQueue];

        public Exception? Failure { get; set; }

        public string? SkipText { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public int Cancelled;

        public async Task TranslateBatchAsync(
            IReadOnlyList<ChatLine> lines, Lang target, IProgress<(long LineId, string Delta)> progress, CancellationToken ct)
        {
            BatchQueue.Enqueue(lines.ToArray());
            if (Failure is { } f)
            {
                throw f;
            }

            var missing = new List<long>();
            foreach (var line in lines)
            {
                if (line.Original == SkipText)
                {
                    missing.Add(line.Id);
                    continue;
                }

                progress.Report((line.Id, "EN("));
                if (Gate is { } gate)
                {
                    try
                    {
                        await gate.Task.WaitAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref Cancelled);
                        throw;
                    }
                }

                progress.Report((line.Id, line.Original + ")"));
            }

            if (missing.Count > 0)
            {
                throw new IncompleteBatchException(missing);
            }
        }

        public Task<OutgoingDraft> TranslateOutgoingAsync(OutgoingDraft draft, CancellationToken ct) =>
            Task.FromResult(draft with { JapaneseText = "JA(" + draft.EnglishText + ")" });
    }
}
