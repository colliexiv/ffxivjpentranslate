using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JpEnChat.Models;
using JpEnChat.Translation;

namespace JpEnChat.Ui;

/// <summary>A line the session sent successfully.</summary>
/// <param name="Command">The full chat-box line that was sent (prefix + text).</param>
/// <param name="English">The English the user typed (without prefix).</param>
/// <param name="Japanese">The Japanese that was sent; empty when <paramref name="Translated"/> is false.</param>
/// <param name="Translated">False for "send the English as typed" (<see cref="OutgoingSession.SendAsIs"/>).</param>
internal sealed record SentMessage(string Command, string English, string Japanese, bool Translated);

/// <summary>
/// The outgoing EN→JA state machine (PLAN §4.1), shared by the main window's composer and the quick-translate popup.
/// No ImGui; <see cref="OutgoingPanel"/> draws it.
/// </summary>
/// <remarks>
/// <para><b>States</b> (<see cref="OutgoingState"/>): Editing → Translating → Confirming → Sending → Editing.
/// A failed translation returns to Editing with <see cref="Error"/> set; a failed send returns to Confirming
/// (or Editing for <see cref="SendAsIs"/>) with <see cref="Error"/> set.</para>
/// <para><b>Threading.</b> All members are called on the draw/framework thread. The translator runs in
/// <c>Task.Run</c>; its result is marshalled back through the <c>post</c> delegate and dropped if a newer request,
/// a cancel or dispose happened in between (generation counter).</para>
/// </remarks>
internal sealed class OutgoingSession : IDisposable
{
    private const int MaxBytes = IChatSender.MaxMessageBytes;

    private readonly IOutgoingTranslator translator;
    private readonly Func<string, Task> send;
    private readonly Action<Action> post;
    private readonly Func<string?> channelPrefix;
    private readonly Func<string> currentEnglish;

    private CancellationTokenSource? cts;
    private int generation;
    private bool disposed;
    private bool focusJapanese;

    /// <param name="translator">EN→JA structured translation.</param>
    /// <param name="send">Sends one full chat-box line; must do the game call on the framework thread.</param>
    /// <param name="post">Runs an action on the framework thread (completion callbacks).</param>
    /// <param name="register">Initial register, one of <see cref="Registers"/>.</param>
    /// <param name="channelPrefix">Prefix (with trailing space) for the line being sent, read at translate and send
    /// time; <c>null</c> means a tell without a target, which blocks sending.</param>
    /// <param name="currentEnglish">The English currently shown to the user, used to re-translate after a register
    /// change and to decide whether the Japanese box should take focus.</param>
    public OutgoingSession(
        IOutgoingTranslator translator,
        Func<string, Task> send,
        Action<Action> post,
        string register,
        Func<string?> channelPrefix,
        Func<string> currentEnglish)
    {
        this.translator = translator ?? throw new ArgumentNullException(nameof(translator));
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.post = post ?? throw new ArgumentNullException(nameof(post));
        this.channelPrefix = channelPrefix ?? throw new ArgumentNullException(nameof(channelPrefix));
        this.currentEnglish = currentEnglish ?? throw new ArgumentNullException(nameof(currentEnglish));
        Register = register == Registers.Casual ? Registers.Casual : Registers.Polite;
    }

    /// <summary>Raised when a translation arrives (state is now Confirming).</summary>
    public event Action? Translated;

    /// <summary>Raised when a translation fails (state is now Editing, <see cref="Error"/> set).</summary>
    public event Action? TranslationFailed;

    /// <summary>Raised after a successful send (state is now Editing, draft cleared).</summary>
    public event Action<SentMessage>? Sent;

    /// <summary>Raised when a send fails (<see cref="Error"/> set).</summary>
    public event Action? SendFailed;

    public OutgoingState State { get; private set; } = OutgoingState.Editing;

    /// <summary>The editable Japanese (Confirming). The panel writes user edits here.</summary>
    public string Japanese { get; set; } = string.Empty;

    /// <summary>The last translation result while Confirming; null otherwise.</summary>
    public OutgoingDraft? Draft { get; private set; }

    /// <summary>Requested register for the next translation; one of <see cref="Registers"/>.</summary>
    public string Register { get; private set; }

    /// <summary>The English of the request in flight (or of the last request).</summary>
    public string RequestedEnglish { get; private set; } = string.Empty;

    /// <summary>User-facing error, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether anything should be drawn in the breakdown panel.</summary>
    public bool PanelVisible => State != OutgoingState.Editing || Error != null;

    /// <summary>True once after a translation arrived and the Japanese box should take keyboard focus.</summary>
    public bool ConsumeFocusJapanese()
    {
        var value = focusJapanese;
        focusJapanese = false;
        return value;
    }

    /// <summary>Cancels any request in flight and starts translating <paramref name="text"/> (→ Translating).</summary>
    public void StartTranslation(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (disposed)
        {
            return;
        }

        CancelInFlight();

        var request = new OutgoingDraft
        {
            EnglishText = text,
            Register = Register,
            ChannelPrefix = channelPrefix() ?? string.Empty,
        };

        RequestedEnglish = text;
        Error = null;
        State = OutgoingState.Translating;
        var gen = ++generation;
        var source = new CancellationTokenSource();
        cts = source;
        var token = source.Token;

        _ = Task.Run(() => TranslateAsync(request, gen, token), CancellationToken.None);
    }

    /// <summary>Changes the register; re-translates when it changed and a translation is shown or in flight.</summary>
    public void SetRegister(string register)
    {
        var value = register == Registers.Casual ? Registers.Casual : Registers.Polite;
        if (value == Register)
        {
            return;
        }

        Register = value;
        if (State is OutgoingState.Confirming or OutgoingState.Translating)
        {
            Retranslate();
        }
    }

    /// <summary>Translates the current English again (falls back to the last request's English).</summary>
    public void Retranslate()
    {
        var text = currentEnglish().Trim();
        if (text.Length == 0)
        {
            text = Draft?.EnglishText ?? RequestedEnglish;
        }

        if (text.Length > 0)
        {
            StartTranslation(text);
        }
    }

    /// <summary>Sends prefix + the (possibly edited) Japanese. Only while Confirming; checks prefix, emptiness, size.</summary>
    public void Send()
    {
        if (State != OutgoingState.Confirming || Draft == null)
        {
            return;
        }

        var ja = Japanese.Trim();
        var prefix = channelPrefix();
        if (prefix == null)
        {
            Error = "Enter the tell target as Name Surname@World.";
            return;
        }

        if (ja.Length == 0)
        {
            Error = "Nothing to send.";
            return;
        }

        var command = prefix + ja;
        var bytes = Encoding.UTF8.GetByteCount(command);
        if (bytes > MaxBytes)
        {
            Error = $"Too long for one chat line: {bytes}/{MaxBytes} bytes. Shorten the Japanese.";
            return;
        }

        StartSend(new SentMessage(command, Draft.EnglishText, ja, true), OutgoingState.Confirming);
    }

    /// <summary>
    /// Cancels any translation and sends <paramref name="command"/> exactly as given (the user's English, untranslated).
    /// Ignored while a send is in progress.
    /// </summary>
    public void SendAsIs(string command, string english)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(english);
        if (disposed || State == OutgoingState.Sending)
        {
            return;
        }

        CancelInFlight();
        Draft = null;
        Japanese = string.Empty;
        StartSend(new SentMessage(command, english, string.Empty, false), OutgoingState.Editing);
    }

    /// <summary>
    /// Esc: Translating → cancel (Editing); Confirming → discard the translation (Editing); Editing → dismiss the
    /// error. Ignored while Sending (cannot be recalled).
    /// </summary>
    public void Escape()
    {
        switch (State)
        {
            case OutgoingState.Translating:
                CancelInFlight();
                State = OutgoingState.Editing;
                break;

            case OutgoingState.Confirming:
                Draft = null;
                Japanese = string.Empty;
                Error = null;
                State = OutgoingState.Editing;
                break;

            case OutgoingState.Editing:
                Error = null;
                break;

            case OutgoingState.Sending:
                break;
        }
    }

    /// <summary>Back to an empty Editing state. Cancels a translation; a send already handed to the game still completes, but its result is ignored.</summary>
    public void Reset()
    {
        CancelInFlight();
        Draft = null;
        Japanese = string.Empty;
        RequestedEnglish = string.Empty;
        Error = null;
        focusJapanese = false;
        State = OutgoingState.Editing;
    }

    /// <summary>UTF-8 size of the line that <see cref="Send"/> would send, given the prefix's size.</summary>
    public int CommandByteCount(int prefixBytes) => prefixBytes + Encoding.UTF8.GetByteCount(Japanese.AsSpan().Trim());

    public void Dispose()
    {
        disposed = true;
        CancelInFlight();
    }

    private void StartSend(SentMessage message, OutgoingState stateOnFailure)
    {
        State = OutgoingState.Sending;
        Error = null;
        var gen = ++generation;

        Task task;
        try
        {
            task = send(message.Command);
        }
        catch (Exception ex)
        {
            OnSent(gen, message, stateOnFailure, ex);
            return;
        }

        if (task.IsCompleted)
        {
            OnSent(gen, message, stateOnFailure, task.Exception?.GetBaseException());
            return;
        }

        task.ContinueWith(
            t => post(() => OnSent(gen, message, stateOnFailure, t.Exception?.GetBaseException())),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task TranslateAsync(OutgoingDraft request, int gen, CancellationToken token)
    {
        try
        {
            var result = await translator.TranslateOutgoingAsync(request, token).ConfigureAwait(false);
            post(() => OnTranslated(gen, result, null));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Esc, a newer request or dispose; the state was already changed by whoever cancelled.
        }
        catch (Exception ex)
        {
            post(() => OnTranslated(gen, null, ex));
        }
    }

    private void OnTranslated(int gen, OutgoingDraft? result, Exception? ex)
    {
        if (disposed || gen != generation || State != OutgoingState.Translating)
        {
            return; // superseded or cancelled
        }

        DisposeCts();

        if (result == null)
        {
            State = OutgoingState.Editing;
            Error = $"Translation failed: {Describe(ex)}";
            TranslationFailed?.Invoke();
            return;
        }

        Draft = result;
        Japanese = result.JapaneseText;
        Error = null;
        State = OutgoingState.Confirming;

        // Move focus to the JA box so the second Enter sends, unless the user kept typing English meanwhile.
        focusJapanese = string.Equals(currentEnglish().Trim(), result.EnglishText, StringComparison.Ordinal);
        Translated?.Invoke();
    }

    private void OnSent(int gen, SentMessage message, OutgoingState stateOnFailure, Exception? ex)
    {
        if (disposed || gen != generation || State != OutgoingState.Sending)
        {
            return;
        }

        if (ex != null)
        {
            State = stateOnFailure;
            Error = $"Send failed: {Describe(ex)}";
            focusJapanese = stateOnFailure == OutgoingState.Confirming;
            SendFailed?.Invoke();
            return;
        }

        Japanese = string.Empty;
        Draft = null;
        RequestedEnglish = string.Empty;
        State = OutgoingState.Editing;
        Sent?.Invoke(message);
    }

    private void CancelInFlight()
    {
        generation++;
        if (cts == null)
        {
            return;
        }

        cts.Cancel();
        DisposeCts();
    }

    private void DisposeCts()
    {
        cts?.Dispose();
        cts = null;
    }

    internal static string Describe(Exception? ex) => ex switch
    {
        null => "unknown error",
        _ when string.IsNullOrWhiteSpace(ex.Message) => ex.GetType().Name,
        _ => ex.Message,
    };
}
