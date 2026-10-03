using System;
using System.Collections.Generic;
using System.Threading;

namespace JpEnChat.Models;

/// <summary>
/// Bounded, ordered store of <see cref="ChatLine"/> rows shown by the main window (PLAN §4).
/// </summary>
/// <remarks>
/// <para>Writers (ingest and the outgoing send path) call <see cref="Add"/> on the framework thread.
/// The window reads through <see cref="Snapshot"/> on the draw thread. All list access happens under one short
/// lock, so the type is safe even if a writer ends up on another thread.</para>
/// <para><see cref="Snapshot"/> returns a cached array that is rebuilt only when <see cref="Version"/> changed
/// since the last call, so an idle frame allocates nothing and a frame after new rows allocates one array.</para>
/// <para>The rows themselves are shared, not copied: their translation fields keep changing after they are
/// added (see the threading contract on <see cref="ChatLine"/>).</para>
/// </remarks>
public sealed class ChatLog
{
    /// <summary>Lower bound applied to the configured maximum so a typo cannot empty the log.</summary>
    public const int MinMaxLines = 100;

    private readonly object sync = new();
    private readonly List<ChatLine> lines = [];
    private readonly Func<int> maxLines;
    private ChatLine[] snapshot = [];
    private long snapshotVersion;
    private long version;

    /// <param name="maxLines">
    /// Read on every <see cref="Add"/> (e.g. <c>() =&gt; configuration.MaxLogLines</c>), so a settings change applies
    /// on the next row. Values below <see cref="MinMaxLines"/> are raised to it.
    /// </param>
    public ChatLog(Func<int> maxLines)
    {
        ArgumentNullException.ThrowIfNull(maxLines);
        this.maxLines = maxLines;
    }

    /// <summary>Incremented on every <see cref="Add"/> and <see cref="Clear"/>; lets readers detect new rows cheaply.</summary>
    public long Version => Interlocked.Read(ref version);

    public int Count
    {
        get
        {
            lock (sync)
            {
                return lines.Count;
            }
        }
    }

    /// <summary>Appends <paramref name="line"/>, dropping the oldest rows past the configured maximum.</summary>
    public void Add(ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        lock (sync)
        {
            lines.Add(line);
            var excess = lines.Count - Math.Max(MinMaxLines, maxLines());
            if (excess > 0)
            {
                lines.RemoveRange(0, excess);
            }

            Interlocked.Increment(ref version);
        }
    }

    /// <summary>Removes every row.</summary>
    public void Clear()
    {
        lock (sync)
        {
            lines.Clear();
            Interlocked.Increment(ref version);
        }
    }

    /// <summary>
    /// Rows in insertion order. The returned list is immutable and stays valid after later writes; call again to
    /// see them. Allocates only when <see cref="Version"/> changed since the previous call.
    /// </summary>
    public IReadOnlyList<ChatLine> Snapshot()
    {
        lock (sync)
        {
            if (snapshotVersion != version)
            {
                snapshot = lines.ToArray();
                snapshotVersion = version;
            }

            return snapshot;
        }
    }
}
