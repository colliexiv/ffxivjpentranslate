using System;
using System.Collections.Generic;
using System.Text;

namespace JpEnChat.Translation;

/// <summary>
/// Incremental parser for a streamed numbered-lines answer (<c>"1: ...\n2: ...\n"</c>) that routes text to
/// the right chat line as soon as its <c>N:</c> prefix has been seen.
/// </summary>
/// <remarks>
/// <para>Tolerated variations: leading spaces, fullwidth digits, <c>N:</c> without a space, fullwidth colon
/// <c>：</c>, and <c>N.</c> / <c>N)</c>; spaces after the separator are skipped. A prefix is only accepted for a
/// number in 1..count that has not been used yet; a well-formed prefix with any other number drops that
/// physical line (the model hallucinated an extra line or repeated one).</para>
/// <para>A physical line that does not start with a prefix is a wrap of the current numbered line: it is
/// buffered until its newline and appended with a single space. Lines that are only a code fence or whitespace
/// are ignored, and unnumbered text before the first prefix is dropped as commentary. Exception: a one-line
/// batch whose answer never contains a prefix is taken as line 1 at <see cref="Complete"/>.</para>
/// <para>Deltas are coalesced per <see cref="Push"/> call: each call reports at most one delta per line run, in
/// stream order. Not thread-safe; one instance per request.</para>
/// </remarks>
public sealed class NumberedLineRouter
{
    private const int MaxPrefixChars = 12;

    private readonly long[] ids;
    private readonly bool[] started;
    private readonly bool[] hasContent;
    private readonly IProgress<(long LineId, string Delta)> progress;
    private readonly StringBuilder prefix = new();
    private readonly StringBuilder continuation = new();
    private readonly StringBuilder pendingText = new();
    private readonly StringBuilder preamble = new();

    private int current = -1;
    private int pendingIndex = -1;
    private Mode mode = Mode.LineStart;
    private bool skipSpaces;
    private bool completed;

    public NumberedLineRouter(IReadOnlyList<long> lineIds, IProgress<(long LineId, string Delta)> progress)
    {
        ArgumentNullException.ThrowIfNull(lineIds);
        ArgumentNullException.ThrowIfNull(progress);
        ids = [.. lineIds];
        started = new bool[ids.Length];
        hasContent = new bool[ids.Length];
        this.progress = progress;
    }

    private enum Mode
    {
        LineStart,
        Numbered,
        Continuation,
        Ignored,
    }

    private enum PrefixResult
    {
        Incomplete,
        Accepted,
        Rejected,
        NotPrefix,
    }

    /// <summary>Feeds one streamed text delta.</summary>
    public void Push(string delta)
    {
        ObjectDisposedException.ThrowIf(completed, this);
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        foreach (var c in delta)
        {
            Process(c);
        }

        FlushPending();
    }

    /// <summary>Flushes buffered text at end of stream. Call exactly once; afterwards read <see cref="MissingIds"/>.</summary>
    public void Complete()
    {
        if (completed)
        {
            return;
        }

        EndPhysicalLine();

        // A one-line batch answered without any "1:" prefix: take the whole answer as line 1.
        if (ids.Length == 1 && current < 0 && preamble.Length > 0)
        {
            foreach (var ch in preamble.ToString())
            {
                Emit(0, ch);
            }
        }

        FlushPending();
        completed = true;
    }

    /// <summary>Ids of lines that received no non-whitespace text, in input order.</summary>
    public IReadOnlyList<long> MissingIds
    {
        get
        {
            var missing = new List<long>();
            for (var i = 0; i < ids.Length; i++)
            {
                if (!hasContent[i])
                {
                    missing.Add(ids[i]);
                }
            }

            return missing;
        }
    }

    private void Process(char c)
    {
        if (c is '\n' or '\r')
        {
            EndPhysicalLine();
            return;
        }

        switch (mode)
        {
            case Mode.LineStart:
                prefix.Append(c);
                switch (ClassifyPrefix(out var number))
                {
                    case PrefixResult.Accepted:
                        current = number - 1;
                        started[current] = true;
                        prefix.Clear();
                        mode = Mode.Numbered;
                        skipSpaces = true;
                        break;
                    case PrefixResult.Rejected:
                        prefix.Clear();
                        mode = Mode.Ignored;
                        break;
                    case PrefixResult.NotPrefix:
                        continuation.Append(prefix);
                        prefix.Clear();
                        mode = Mode.Continuation;
                        break;
                }

                break;
            case Mode.Numbered:
                if (skipSpaces && c is ' ' or '\t' or '　')
                {
                    return;
                }

                skipSpaces = false;
                Emit(current, c);
                break;
            case Mode.Continuation:
                continuation.Append(c);
                break;
            case Mode.Ignored:
                break;
        }
    }

    private void EndPhysicalLine()
    {
        if (mode == Mode.LineStart && prefix.Length > 0)
        {
            continuation.Append(prefix); // e.g. a bare "3" on its own line
            mode = Mode.Continuation;
        }

        if (mode == Mode.Continuation)
        {
            var text = continuation.ToString().Trim();
            if (text.Length > 0 && text.Trim('`').Length > 0)
            {
                if (current < 0)
                {
                    if (preamble.Length > 0)
                    {
                        preamble.Append(' ');
                    }

                    preamble.Append(text);
                }
                else
                {
                    if (hasContent[current])
                    {
                        Emit(current, ' ');
                    }

                    foreach (var ch in text)
                    {
                        Emit(current, ch);
                    }
                }
            }
        }

        prefix.Clear();
        continuation.Clear();
        mode = Mode.LineStart;
        skipSpaces = false;
    }

    private PrefixResult ClassifyPrefix(out int number)
    {
        number = 0;
        var i = 0;
        while (i < prefix.Length && prefix[i] is ' ' or '\t' or '　')
        {
            i++;
        }

        if (i == prefix.Length)
        {
            return prefix.Length > MaxPrefixChars ? PrefixResult.NotPrefix : PrefixResult.Incomplete;
        }

        var digitsStart = i;
        var value = 0;
        while (i < prefix.Length && DigitValue(prefix[i]) is var d && d >= 0)
        {
            value = (value * 10) + d;
            i++;
            if (i - digitsStart > 3)
            {
                return PrefixResult.NotPrefix;
            }
        }

        if (i == digitsStart)
        {
            return PrefixResult.NotPrefix;
        }

        while (i < prefix.Length && prefix[i] is ' ' or '　')
        {
            i++;
        }

        if (i == prefix.Length)
        {
            return prefix.Length > MaxPrefixChars ? PrefixResult.NotPrefix : PrefixResult.Incomplete;
        }

        if (prefix[i] is not (':' or '：' or '.' or '．' or ')' or '）'))
        {
            return PrefixResult.NotPrefix;
        }

        if (value < 1 || value > ids.Length || started[value - 1])
        {
            return PrefixResult.Rejected;
        }

        number = value;
        return PrefixResult.Accepted;
    }

    private static int DigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= '０' and <= '９' => c - '０',
        _ => -1,
    };

    private void Emit(int index, char c)
    {
        if (pendingIndex != index)
        {
            FlushPending();
            pendingIndex = index;
        }

        pendingText.Append(c);
        if (!char.IsWhiteSpace(c))
        {
            hasContent[index] = true;
        }
    }

    private void FlushPending()
    {
        if (pendingIndex >= 0 && pendingText.Length > 0)
        {
            progress.Report((ids[pendingIndex], pendingText.ToString()));
        }

        pendingText.Clear();
        pendingIndex = -1;
    }
}

/// <summary>The model answered fewer numbered lines than were requested.</summary>
public sealed class IncompleteBatchException : Exception
{
    public IncompleteBatchException()
        : this([])
    {
    }

    public IncompleteBatchException(string message)
        : base(message)
    {
        MissingIds = [];
    }

    public IncompleteBatchException(string message, Exception innerException)
        : base(message, innerException)
    {
        MissingIds = [];
    }

    public IncompleteBatchException(IReadOnlyList<long> missingIds)
        : base($"Model returned no output for {missingIds?.Count ?? 0} line(s).")
    {
        MissingIds = missingIds ?? [];
    }

    /// <summary><see cref="Models.ChatLine.Id"/>s that received no translation.</summary>
    public IReadOnlyList<long> MissingIds { get; }
}
