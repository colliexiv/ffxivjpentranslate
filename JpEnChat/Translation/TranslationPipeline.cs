using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JpEnChat.Models;

namespace JpEnChat.Translation;

/// <summary>
/// The client-side gate (PLAN §3.2): language check → exact cache → per-sender debounce → bounded-concurrency
/// translation jobs whose results are marshalled back to the framework thread.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> <see cref="Enqueue"/> and <see cref="Retry"/> are called on the framework thread and
/// may mutate the line synchronously. Debounce timers and jobs run on the thread pool; their only access to a
/// <see cref="ChatLine"/> is reading immutable fields (<see cref="ChatLine.Id"/>, <see cref="ChatLine.Original"/>)
/// and posting mutations through <c>runOnFrameworkThread</c>, which must run actions in submission order
/// (Dalamud's <c>IFramework.RunOnFrameworkThread</c> does). Pending batches are guarded by one lock.</para>
/// <para><b>Batching.</b> Each sender (name + world) has one pending batch and one timer. Every new line restarts
/// the timer at <see cref="Configuration.DebounceMs"/>, capped so that a batch never waits more than
/// <see cref="MaxWaitFactor"/> × the debounce after its first line, and a batch of <see cref="MaxBatchLines"/>
/// lines is sent immediately. Both caps keep a chatty sender from starving their own translations.</para>
/// <para><b>Concurrency.</b> <see cref="Configuration.MaxConcurrency"/> is read once at construction; a change
/// takes effect on the next plugin load.</para>
/// </remarks>
public sealed class TranslationPipeline : IDisposable
{
    /// <summary>A batch with this many lines is sent without waiting for the debounce.</summary>
    public const int MaxBatchLines = 10;

    /// <summary>A batch waits at most this many debounce periods after its first line.</summary>
    public const int MaxWaitFactor = 3;

    /// <summary>Interval of the background cache save.</summary>
    public static readonly TimeSpan CacheSaveInterval = TimeSpan.FromSeconds(60);

    private readonly Configuration config;
    private readonly ILanguageDetector detector;
    private readonly ITranslationCache cache;
    private readonly ITranslator translator;
    private readonly Action<Action> runOnFrameworkThread;
    private readonly ILog log;

    private readonly object gate = new();
    private readonly Dictionary<string, SenderBatch> pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim concurrency;
    private readonly CancellationTokenSource cts = new();
    private readonly CancellationToken shutdown;
    private readonly Timer saveTimer;
    private int activeJobs;
    private bool disposed;

    public TranslationPipeline(
        Configuration config,
        ILanguageDetector detector,
        ITranslationCache cache,
        ITranslator translator,
        Action<Action> runOnFrameworkThread,
        ILog log)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(runOnFrameworkThread);
        ArgumentNullException.ThrowIfNull(log);
        this.config = config;
        this.detector = detector;
        this.cache = cache;
        this.translator = translator;
        this.runOnFrameworkThread = runOnFrameworkThread;
        this.log = log;

        var max = Math.Clamp(config.MaxConcurrency, 1, 16);
        concurrency = new SemaphoreSlim(max, max);
        shutdown = cts.Token;
        saveTimer = new Timer(_ => SaveCache(), null, CacheSaveInterval, CacheSaveInterval);
    }

    /// <summary>Lines waiting in a debounce window (all senders).</summary>
    public int PendingLineCount
    {
        get
        {
            lock (gate)
            {
                return pending.Values.Sum(b => b.Lines.Count);
            }
        }
    }

    /// <summary>Translation jobs queued on or holding the concurrency limit.</summary>
    public int ActiveJobCount => Volatile.Read(ref activeJobs);

    /// <summary>
    /// Gates a newly ingested line. Framework thread only. Non-Japanese → <see cref="TranslationStatus.None"/>;
    /// cache hit → <see cref="TranslationStatus.CacheHit"/> synchronously; otherwise
    /// <see cref="TranslationStatus.Pending"/> and queued in the sender's debounce batch.
    /// </summary>
    /// <remarks>A line whose <see cref="ChatLine.OriginalLang"/> is <see cref="Lang.Unknown"/> is classified with the
    /// detector here, so ingest may skip detection.</remarks>
    public void Enqueue(ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (disposed)
        {
            return;
        }

        var lang = line.OriginalLang == Lang.Unknown ? detector.Detect(line.Original) : line.OriginalLang;
        if (lang != Lang.Ja)
        {
            line.Status = TranslationStatus.None;
            return;
        }

        if (config.CacheEnabled && cache.TryGet(line.Original, TranslationDirection.JaToEn, out var cached))
        {
            line.Translation = cached;
            line.Error = null;
            line.Status = TranslationStatus.CacheHit;
            return;
        }

        line.Translation = string.Empty;
        line.Error = null;
        line.Status = TranslationStatus.Pending;

        var debounceMs = Math.Clamp(config.DebounceMs, 0, 10_000);
        var key = line.SenderName + "@" + line.SenderWorld;
        List<ChatLine>? sendNow = null;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (!pending.TryGetValue(key, out var batch))
            {
                batch = new SenderBatch(key);
                batch.Timer = new Timer(OnDebounceElapsed, batch, Timeout.Infinite, Timeout.Infinite);
                pending[key] = batch;
            }

            batch.Lines.Add(line);
            if (batch.Lines.Count >= MaxBatchLines)
            {
                sendNow = TakeLocked(batch);
            }
            else
            {
                var maxWait = (long)debounceMs * MaxWaitFactor;
                var remaining = Math.Max(0, maxWait - batch.Age.ElapsedMilliseconds);
                batch.Timer!.Change(Math.Min(debounceMs, remaining), Timeout.Infinite);
            }
        }

        if (sendNow is not null)
        {
            StartJob(sendNow);
        }
    }

    /// <summary>Re-sends a <see cref="TranslationStatus.Failed"/> line immediately, bypassing the debounce. Framework thread only.</summary>
    public void Retry(ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (disposed || line.Status != TranslationStatus.Failed)
        {
            return;
        }

        line.Translation = string.Empty;
        line.Error = null;
        line.Status = TranslationStatus.Pending;
        StartJob([line]);
    }

    /// <summary>Outgoing EN→JA; not subject to the incoming concurrency limit. Cancelled on dispose as well as by <paramref name="ct"/>.</summary>
    public async Task<OutgoingDraft> TranslateOutgoingAsync(OutgoingDraft draft, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown);
        return await translator.TranslateOutgoingAsync(draft, linked.Token).ConfigureAwait(false);
    }

    /// <summary>Short, user-facing text for a failed request (shown in the translation cell).</summary>
    public static string DescribeError(Exception ex) => ex switch
    {
        MissingApiKeyException => "no API key",
        IncompleteBatchException => "no output",
        OpenRouterException { StatusCode: { } status } => status switch
        {
            400 => "400 bad request",
            401 => "401 bad key",
            402 => "402 out of credits",
            403 => "403 blocked",
            404 => "404 model not found",
            408 or 504 => "timeout",
            413 => "413 too long",
            429 => "429 rate limited",
            502 => "502 provider error",
            503 => "503 no provider",
            >= 500 => $"{status} server error",
            _ => $"{status} error",
        },
        OpenRouterException => "provider error",
        TimeoutException => "timeout",
        HttpRequestException => "network error",
        OperationCanceledException => "cancelled",
        _ => "error",
    };

    public void Dispose()
    {
        List<SenderBatch> batches;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            batches = [.. pending.Values];
            pending.Clear();
        }

        cts.Cancel();
        foreach (var b in batches)
        {
            b.Taken = true;
            b.Timer?.Dispose();
        }

        saveTimer.Dispose();
        SaveCache();

        // The CTS and semaphore are deliberately not disposed: jobs that are already unwinding still touch them.
    }

    private void OnDebounceElapsed(object? state)
    {
        var batch = (SenderBatch)state!;
        List<ChatLine>? lines;
        lock (gate)
        {
            if (batch.Taken || disposed)
            {
                return;
            }

            lines = TakeLocked(batch);
        }

        StartJob(lines);
    }

    /// <summary>Removes the batch and returns its lines. Caller holds <see cref="gate"/>.</summary>
    private List<ChatLine> TakeLocked(SenderBatch batch)
    {
        batch.Taken = true;
        pending.Remove(batch.Key);
        batch.Timer?.Dispose();
        return batch.Lines;
    }

    private void StartJob(List<ChatLine> lines)
    {
        Interlocked.Increment(ref activeJobs);
        _ = Task.Run(() => RunJobAsync(lines));
    }

    private async Task RunJobAsync(List<ChatLine> lines)
    {
        try
        {
            await concurrency.WaitAsync(shutdown).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Decrement(ref activeJobs);
            return;
        }

        var sw = Stopwatch.StartNew();
        var firstDeltaTicks = 0L;
        try
        {
            var byId = lines.ToDictionary(l => l.Id);
            var progress = new InlineProgress<(long LineId, string Delta)>(p =>
            {
                if (p.Delta.Length == 0 || !byId.TryGetValue(p.LineId, out var line))
                {
                    return;
                }

                Interlocked.CompareExchange(ref firstDeltaTicks, Math.Max(1, sw.ElapsedTicks), 0);
                var delta = p.Delta;
                Post(() =>
                {
                    if (line.Status is TranslationStatus.Pending or TranslationStatus.Streaming)
                    {
                        line.Status = TranslationStatus.Streaming;
                        line.Translation += delta;
                    }
                });
            });

            IReadOnlyCollection<long> missing = [];
            try
            {
                await translator.TranslateBatchAsync(lines, Lang.En, progress, shutdown).ConfigureAwait(false);
            }
            catch (IncompleteBatchException ex)
            {
                missing = ex.MissingIds.ToHashSet();
            }

            var ttft = Interlocked.Read(ref firstDeltaTicks);
            log.Information(
                $"Translated {lines.Count} line(s) in {sw.ElapsedMilliseconds} ms " +
                $"(first token {(ttft == 0 ? "n/a" : $"{ttft * 1000 / Stopwatch.Frequency} ms")}, model {config.Model}" +
                $"{(missing.Count > 0 ? $", {missing.Count} missing" : string.Empty)}).");
            CompleteLines(lines, missing);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Plugin unloading; leave the lines as they are.
        }
        catch (Exception ex)
        {
            var message = DescribeError(ex);
            log.Warning($"Translation of {lines.Count} line(s) failed after {sw.ElapsedMilliseconds} ms: {message} ({ex.GetType().Name}: {ex.Message})");
            Post(() =>
            {
                foreach (var line in lines)
                {
                    if (line.Status is TranslationStatus.Pending or TranslationStatus.Streaming)
                    {
                        line.Status = TranslationStatus.Failed;
                        line.Error = message;
                    }
                }
            });
        }
        finally
        {
            concurrency.Release();
            Interlocked.Decrement(ref activeJobs);
        }
    }

    private void CompleteLines(List<ChatLine> lines, IReadOnlyCollection<long> missing)
    {
        Post(() =>
        {
            foreach (var line in lines)
            {
                if (line.Status is not (TranslationStatus.Pending or TranslationStatus.Streaming))
                {
                    continue;
                }

                var text = line.Translation.Trim();
                if (text.Length == 0 || missing.Contains(line.Id))
                {
                    line.Status = TranslationStatus.Failed;
                    line.Error = "no output";
                    continue;
                }

                line.Translation = text;
                line.Error = null;
                line.Status = TranslationStatus.Done;
                if (config.CacheEnabled)
                {
                    cache.Put(line.Original, TranslationDirection.JaToEn, text);
                }
            }
        });
    }

    private void Post(Action action)
    {
        try
        {
            runOnFrameworkThread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    log.Error(ex, "Translation pipeline update failed on the framework thread.");
                }
            });
        }
        catch (Exception ex)
        {
            log.Error(ex, "Could not schedule a translation update on the framework thread.");
        }
    }

    private void SaveCache()
    {
        try
        {
            cache.SaveIfDirty();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Saving the translation cache failed.");
        }
    }

    private sealed class SenderBatch(string key)
    {
        public string Key { get; } = key;

        public List<ChatLine> Lines { get; } = [];

        public Stopwatch Age { get; } = Stopwatch.StartNew();

        public Timer? Timer { get; set; }

        public bool Taken { get; set; }
    }

    /// <summary>Synchronous <see cref="IProgress{T}"/>: unlike <see cref="Progress{T}"/> it never reorders reports.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
