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
    public async Task StreamedDeltasAppliedOutOfOrderStillProduceCorrectText()
    {
        // Dalamud's RunOnFrameworkThread does not preserve submission order; the pipeline must not depend on it.
        var queued = new List<Action>();
        using var p = new TranslationPipeline(
            new Configuration { DebounceMs = 0, MaxConcurrency = 1, CacheEnabled = false },
            new ScriptLanguageDetector(),
            cache,
            translator,
            action =>
            {
                lock (queued)
                {
                    queued.Add(action);
                }
            },
            log);

        var line = TestUtil.JaLine("こんにちは");
        p.Enqueue(line);
        await TestUtil.WaitUntil(() =>
        {
            lock (queued)
            {
                return queued.Count >= 3 && p.ActiveJobCount == 0; // two deltas + completion
            }
        });

        lock (queued)
        {
            for (var i = queued.Count - 1; i >= 0; i--)
            {
                queued[i]();
            }
        }

        Assert.Equal(TranslationStatus.Done, line.Status);
        Assert.Equal("EN(こんにちは)", line.Translation);
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

    private void OnFramework(Action action)
    {
        lock (frameworkLock)
        {
            action();
        }
    }

    [Fact]
    public async Task PinnedEntryIsCacheHitWithoutCallingTheTranslator()
    {
        cache.Pin("ノ", TranslationDirection.JaToEn, "o/");
        var line = TestUtil.JaLine("ノ");
        Enqueue(line);

        Assert.Equal(TranslationStatus.CacheHit, line.Status); // synchronous
        Assert.Equal("o/", line.Translation);
        await Task.Delay(300);
        Assert.Empty(translator.Batches);
    }

    [Fact]
    public async Task BatchCarriesTheContextCapturedForItsFirstLine()
    {
        var seen = new List<long>();
        using var withContext = new TranslationPipeline(
            new Configuration { DebounceMs = 50, MaxConcurrency = 2, CacheEnabled = false },
            new ScriptLanguageDetector(),
            cache,
            translator,
            OnFramework,
            log,
            first =>
            {
                seen.Add(first.Id);
                return new TranslationContext { Channel = "Party:" + first.Original };
            });

        var a = TestUtil.JaLine("いち");
        var b = TestUtil.JaLine("に");
        OnFramework(() =>
        {
            withContext.Enqueue(a);
            withContext.Enqueue(b);
        });

        await TestUtil.WaitUntil(() => a.Status == TranslationStatus.Done && b.Status == TranslationStatus.Done);
        Assert.Equal([a.Id], seen); // built once, for the batch's first line, at enqueue time
        Assert.Equal("Party:いち", Assert.Single(translator.Contexts)!.Channel);
    }

    [Fact]
    public async Task PinnedEntryAppliesWhenTheCacheIsOff()
    {
        using var noCache = new TranslationPipeline(
            new Configuration { DebounceMs = 50, MaxConcurrency = 2, CacheEnabled = false },
            new ScriptLanguageDetector(),
            cache,
            translator,
            OnFramework,
            log);
        cache.Put("一", TranslationDirection.JaToEn, "one");
        cache.Pin("ノ", TranslationDirection.JaToEn, "o/");

        var plain = TestUtil.JaLine("一");
        var pinned = TestUtil.JaLine("ノ");
        OnFramework(() =>
        {
            noCache.Enqueue(plain);
            noCache.Enqueue(pinned);
        });

        Assert.Equal(TranslationStatus.Pending, plain.Status); // ordinary cache entries are ignored
        Assert.Equal(TranslationStatus.CacheHit, pinned.Status);
        Assert.Equal("o/", pinned.Translation);
        await TestUtil.WaitUntil(() => plain.Status == TranslationStatus.Done);
        Assert.DoesNotContain(translator.Batches.SelectMany(b => b), l => l.Id == pinned.Id);
    }

    [Fact]
    public void PinnedRuleAppliesToLinesThatAreNotJapanese()
    {
        cache.Pin("88", TranslationDirection.JaToEn, "bye bye");
        var bye = new ChatLine { Original = "88", SenderName = "X" }; // Lang.Other
        var gg = new ChatLine { Original = "gg", OriginalLang = Lang.En, SenderName = "X" };
        Enqueue(bye);
        Enqueue(gg);

        Assert.Equal(TranslationStatus.CacheHit, bye.Status);
        Assert.Equal("bye bye", bye.Translation);
        Assert.Equal(TranslationStatus.None, gg.Status);
        Assert.Equal(0, pipeline.PendingLineCount);
    }

    [Fact]
    public async Task CorrectionSticksWhenAStaleJobCompletes()
    {
        translator.Gate = new TaskCompletionSource();
        var line = TestUtil.JaLine("練習");
        Enqueue(line);
        await TestUtil.WaitUntil(() => line.Status == TranslationStatus.Streaming);

        OnFramework(() => pipeline.Correct(line, "  practice run\n "));
        Assert.Equal(TranslationStatus.Corrected, line.Status);
        Assert.Equal("practice run", line.Translation);

        translator.Gate.SetResult();
        await TestUtil.WaitUntil(() => pipeline.ActiveJobCount == 0);
        await Task.Delay(50);

        Assert.Equal(TranslationStatus.Corrected, line.Status);
        Assert.Equal("practice run", line.Translation);
        Assert.True(cache.TryGetEntry(cache.CreateKey("練習", TranslationDirection.JaToEn), out var entry));
        Assert.True(entry.Pinned);
        Assert.Equal("practice run", entry.Translation);
        Assert.True(OnFrameworkGet(() => pipeline.IsPinned(line)));

        var again = TestUtil.JaLine("練習！");
        Enqueue(again);
        Assert.Equal(TranslationStatus.CacheHit, again.Status);
        Assert.Equal("practice run", again.Translation);
    }

    [Fact]
    public async Task FinishedJobDoesNotOverwriteAPinMadeMeanwhile()
    {
        translator.Gate = new TaskCompletionSource();
        var line = TestUtil.JaLine("初見");
        Enqueue(line);
        await TestUtil.WaitUntil(() => line.Status == TranslationStatus.Streaming);

        cache.Pin("初見", TranslationDirection.JaToEn, "first time");
        translator.Gate.SetResult();
        await TestUtil.WaitUntil(() => line.Status == TranslationStatus.Done);

        Assert.Equal("EN(初見)", line.Translation);
        Assert.True(cache.TryGetPinned("初見", TranslationDirection.JaToEn, out var pinned));
        Assert.Equal("first time", pinned);
    }

    [Fact]
    public async Task PinAndUnpinFromALogRow()
    {
        var line = TestUtil.JaLine("散開");
        Enqueue(line);
        await TestUtil.WaitUntil(() => line.Status == TranslationStatus.Done);

        Assert.False(OnFrameworkGet(() => pipeline.IsPinned(line)));
        Assert.True(OnFrameworkGet(() => pipeline.Pin(line)));
        Assert.True(cache.TryGetPinned("散開", TranslationDirection.JaToEn, out var pinned));
        Assert.Equal("EN(散開)", pinned);

        OnFramework(() => pipeline.Unpin(line));
        Assert.False(OnFrameworkGet(() => pipeline.IsPinned(line)));
        Assert.True(cache.TryGet("散開", TranslationDirection.JaToEn, out _)); // still cached

        var pending = TestUtil.JaLine("頭割り");
        pending.Status = TranslationStatus.Pending;
        Assert.False(OnFrameworkGet(() => pipeline.Pin(pending)));
    }

    [Fact]
    public async Task CorrectingASentRowPinsEnToJaAndOutgoingUsesIt()
    {
        var sent = new ChatLine
        {
            Original = "o/",
            OriginalLang = Lang.En,
            Translation = "/in",
            Status = TranslationStatus.Done,
            IsSentByPlugin = true,
        };
        OnFramework(() => pipeline.Correct(sent, "ノ"));

        Assert.Equal(TranslationStatus.Corrected, sent.Status);
        Assert.True(cache.TryGetPinned("O/", TranslationDirection.EnToJa, out var ja));
        Assert.Equal("ノ", ja);
        Assert.False(cache.TryGetPinned("o/", TranslationDirection.JaToEn, out _));

        var result = await pipeline.TranslateOutgoingAsync(
            new OutgoingDraft { EnglishText = "O/ ", ChannelPrefix = "/p " }, CancellationToken.None);
        Assert.Equal("ノ", result.JapaneseText);
        Assert.Equal(TranslationPipeline.FixedOutgoingNote, result.BackTranslation);
        Assert.Empty(result.Segments);
        Assert.Equal("/p ノ", result.ToChatCommand());

        var other = await pipeline.TranslateOutgoingAsync(new OutgoingDraft { EnglishText = "hi" }, CancellationToken.None);
        Assert.Equal("JA(hi)", other.JapaneseText); // no pin: the translator is asked
    }

    private T OnFrameworkGet<T>(Func<T> func)
    {
        lock (frameworkLock)
        {
            return func();
        }
    }

    /// <summary>Echo translator: streams "EN(" + text + ")" in two deltas per line.</summary>
    private sealed class FakeTranslator : ITranslator
    {
        public ConcurrentQueue<IReadOnlyList<ChatLine>> BatchQueue { get; } = new();

        public ConcurrentQueue<TranslationContext?> Contexts { get; } = new();

        public List<IReadOnlyList<ChatLine>> Batches => [.. BatchQueue];

        public Exception? Failure { get; set; }

        public string? SkipText { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public int Cancelled;

        public async Task TranslateBatchAsync(
            IReadOnlyList<ChatLine> lines, Lang target, TranslationContext? context, IProgress<(long LineId, string Delta)> progress, CancellationToken ct)
        {
            Contexts.Enqueue(context);
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
