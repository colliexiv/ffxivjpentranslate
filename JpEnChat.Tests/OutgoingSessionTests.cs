using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JpEnChat.Models;
using JpEnChat.Ui;
using Xunit;

namespace JpEnChat.Tests;

public class OutgoingSessionTests
{
    /// <summary>Translator whose results the test releases one by one.</summary>
    private sealed class FakeTranslator : IOutgoingTranslator
    {
        public ConcurrentQueue<(OutgoingDraft Draft, TaskCompletionSource<OutgoingDraft> Result, CancellationToken Ct)> Calls { get; } = new();

        public Task<OutgoingDraft> TranslateOutgoingAsync(OutgoingDraft draft, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<OutgoingDraft>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled(ct));
            Calls.Enqueue((draft, tcs, ct));
            return tcs.Task;
        }
    }

    /// <summary>Owns a session plus a "framework thread" queue the test pumps explicitly.</summary>
    private sealed class Harness
    {
        private readonly ConcurrentQueue<Action> posted = new();

        public Harness(string? prefix = "/p ", Func<string, Task>? send = null)
        {
            Prefix = prefix;
            Send = send ?? (command =>
            {
                Sent.Add(command);
                return Task.CompletedTask;
            });
            Session = new OutgoingSession(Translator, c => Send(c), posted.Enqueue, Registers.Polite, () => Prefix, () => English);
            Session.Sent += m => SentMessages.Add(m);
        }

        public FakeTranslator Translator { get; } = new();

        public OutgoingSession Session { get; }

        public string? Prefix { get; set; }

        public string English { get; set; } = string.Empty;

        public Func<string, Task> Send { get; set; }

        public List<string> Sent { get; } = [];

        public List<SentMessage> SentMessages { get; } = [];

        public async Task<(OutgoingDraft Draft, TaskCompletionSource<OutgoingDraft> Result, CancellationToken Ct)> NextCall()
        {
            await TestUtil.WaitUntil(() => !Translator.Calls.IsEmpty);
            Translator.Calls.TryDequeue(out var call);
            return call;
        }

        public async Task PumpUntil(Func<bool> condition)
        {
            var deadline = Environment.TickCount64 + 5000;
            while (true)
            {
                while (posted.TryDequeue(out var action))
                {
                    action();
                }

                if (condition())
                {
                    return;
                }

                if (Environment.TickCount64 > deadline)
                {
                    throw new TimeoutException("Condition not met in time.");
                }

                await Task.Delay(5);
            }
        }

        public async Task PumpAll()
        {
            await Task.Delay(30);
            while (posted.TryDequeue(out var action))
            {
                action();
            }
        }

        public async Task TranslateToConfirming(string english, string japanese)
        {
            English = english;
            Session.StartTranslation(english);
            var call = await NextCall();
            call.Result.SetResult(call.Draft with { JapaneseText = japanese, BackTranslation = "back" });
            await PumpUntil(() => Session.State == OutgoingState.Confirming);
        }
    }

    [Fact]
    public async Task TranslatesConfirmsAndSends()
    {
        var h = new Harness();
        h.English = "hello";
        var translated = 0;
        h.Session.Translated += () => translated++;

        h.Session.StartTranslation("hello");
        Assert.Equal(OutgoingState.Translating, h.Session.State);

        var call = await h.NextCall();
        Assert.Equal("hello", call.Draft.EnglishText);
        Assert.Equal("/p ", call.Draft.ChannelPrefix);
        Assert.Equal(Registers.Polite, call.Draft.Register);

        call.Result.SetResult(call.Draft with { JapaneseText = "こんにちは" });
        await h.PumpUntil(() => h.Session.State == OutgoingState.Confirming);
        Assert.Equal(1, translated);
        Assert.Equal("こんにちは", h.Session.Japanese);
        Assert.True(h.Session.ConsumeFocusJapanese());
        Assert.False(h.Session.ConsumeFocusJapanese());

        h.Session.Japanese = " こんにちは！ ";
        h.Session.Send();

        Assert.Equal(["/p こんにちは！"], h.Sent);
        Assert.Equal(OutgoingState.Editing, h.Session.State);
        var sent = Assert.Single(h.SentMessages);
        Assert.Equal("hello", sent.English);
        Assert.Equal("こんにちは！", sent.Japanese);
        Assert.True(sent.Translated);
        Assert.Null(h.Session.Draft);
        Assert.Equal(string.Empty, h.Session.Japanese);
    }

    [Fact]
    public async Task NoFocusWhenUserKeptTyping()
    {
        var h = new Harness();
        h.English = "hello";
        h.Session.StartTranslation("hello");
        var call = await h.NextCall();
        h.English = "hello the";
        call.Result.SetResult(call.Draft with { JapaneseText = "こんにちは" });
        await h.PumpUntil(() => h.Session.State == OutgoingState.Confirming);
        Assert.False(h.Session.ConsumeFocusJapanese());
    }

    [Fact]
    public async Task EscapeWhileTranslatingCancelsAndDropsTheLateResult()
    {
        var h = new Harness();
        h.Session.StartTranslation("hello");
        var call = await h.NextCall();

        h.Session.Escape();
        Assert.Equal(OutgoingState.Editing, h.Session.State);
        Assert.True(call.Ct.IsCancellationRequested);

        call.Result.TrySetResult(call.Draft with { JapaneseText = "遅い" });
        await h.PumpAll();
        Assert.Equal(OutgoingState.Editing, h.Session.State);
        Assert.Null(h.Session.Draft);
    }

    [Fact]
    public async Task EscapeWhileConfirmingDiscards()
    {
        var h = new Harness();
        await h.TranslateToConfirming("hello", "こんにちは");

        h.Session.Escape();
        Assert.Equal(OutgoingState.Editing, h.Session.State);
        Assert.Null(h.Session.Draft);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task NewerRequestSupersedesOlder()
    {
        var h = new Harness();
        h.Session.StartTranslation("first");
        var first = await h.NextCall();
        h.English = "second";
        h.Session.StartTranslation("second");
        var second = await h.NextCall();
        Assert.True(first.Ct.IsCancellationRequested);

        second.Result.SetResult(second.Draft with { JapaneseText = "二番目" });
        await h.PumpUntil(() => h.Session.State == OutgoingState.Confirming);
        Assert.Equal("二番目", h.Session.Japanese);
        Assert.Equal("second", h.Session.Draft!.EnglishText);
    }

    [Fact]
    public async Task TranslationFailureReturnsToEditingWithError()
    {
        var h = new Harness();
        var failed = 0;
        h.Session.TranslationFailed += () => failed++;
        h.Session.StartTranslation("hello");
        var call = await h.NextCall();
        call.Result.SetException(new InvalidOperationException("402: credits"));

        await h.PumpUntil(() => h.Session.State == OutgoingState.Editing);
        Assert.Equal(1, failed);
        Assert.Contains("402: credits", h.Session.Error);
        Assert.True(h.Session.PanelVisible);

        h.Session.Escape();
        Assert.Null(h.Session.Error);
        Assert.False(h.Session.PanelVisible);
    }

    [Fact]
    public async Task SendFailureReturnsToConfirming()
    {
        var h = new Harness(send: _ => Task.FromException(new ArgumentException("bad characters")));
        var failed = 0;
        h.Session.SendFailed += () => failed++;
        await h.TranslateToConfirming("hello", "こんにちは");
        h.Session.ConsumeFocusJapanese();

        h.Session.Send();
        Assert.Equal(OutgoingState.Confirming, h.Session.State);
        Assert.Contains("bad characters", h.Session.Error);
        Assert.Equal(1, failed);
        Assert.True(h.Session.ConsumeFocusJapanese());
        Assert.Empty(h.SentMessages);
    }

    [Fact]
    public async Task AsynchronousSendCompletesThroughPost()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new Harness(send: _ => gate.Task);
        await h.TranslateToConfirming("hello", "こんにちは");

        h.Session.Send();
        Assert.Equal(OutgoingState.Sending, h.Session.State);
        h.Session.Escape(); // ignored while sending
        Assert.Equal(OutgoingState.Sending, h.Session.State);

        gate.SetResult();
        await h.PumpUntil(() => h.Session.State == OutgoingState.Editing);
        Assert.Single(h.SentMessages);
    }

    [Fact]
    public async Task SendIsBlockedWhenTooLongOrEmptyOrNoTellTarget()
    {
        var h = new Harness();
        await h.TranslateToConfirming("hello", new string('あ', 200));
        h.Session.Send();
        Assert.Equal(OutgoingState.Confirming, h.Session.State);
        Assert.Contains("Too long", h.Session.Error);
        Assert.True(h.Session.CommandByteCount(3) > 500);

        h.Session.Japanese = "  ";
        h.Session.Send();
        Assert.Equal("Nothing to send.", h.Session.Error);

        h.Session.Japanese = "こんにちは";
        h.Prefix = null;
        h.Session.Send();
        Assert.Contains("tell target", h.Session.Error);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task EmptyPrefixSendsJapaneseAlone()
    {
        var h = new Harness(prefix: string.Empty);
        await h.TranslateToConfirming("hello", "こんにちは");
        h.Session.Send();
        Assert.Equal(["こんにちは"], h.Sent);
    }

    [Fact]
    public async Task RegisterChangeRetranslates()
    {
        var h = new Harness();
        await h.TranslateToConfirming("hello", "こんにちは");

        h.Session.SetRegister(Registers.Casual);
        Assert.Equal(OutgoingState.Translating, h.Session.State);
        var call = await h.NextCall();
        Assert.Equal(Registers.Casual, call.Draft.Register);
        Assert.Equal("hello", call.Draft.EnglishText);

        // Same register again: nothing happens.
        h.Session.SetRegister(Registers.Casual);
        Assert.True(h.Translator.Calls.IsEmpty);
    }

    [Fact]
    public async Task SendAsIsCancelsTranslationAndSendsTheTypedLine()
    {
        var h = new Harness();
        h.Session.StartTranslation("hello");
        var call = await h.NextCall();

        h.Session.SendAsIs("/p hello", "hello");
        Assert.True(call.Ct.IsCancellationRequested);
        Assert.Equal(["/p hello"], h.Sent);
        var sent = Assert.Single(h.SentMessages);
        Assert.False(sent.Translated);
        Assert.Equal(string.Empty, sent.Japanese);
        Assert.Equal(OutgoingState.Editing, h.Session.State);
    }

    [Fact]
    public void SendAsIsFailureShowsErrorInEditing()
    {
        var h = new Harness(send: _ => throw new InvalidOperationException("not on framework thread"));
        h.Session.SendAsIs("hello", "hello");
        Assert.Equal(OutgoingState.Editing, h.Session.State);
        Assert.Contains("not on framework thread", h.Session.Error);
        Assert.Empty(h.SentMessages);
    }

    [Fact]
    public async Task ResetAndDisposeDropLateResults()
    {
        var h = new Harness();
        h.Session.StartTranslation("hello");
        var call = await h.NextCall();
        h.Session.Reset();
        Assert.Equal(OutgoingState.Editing, h.Session.State);
        Assert.True(call.Ct.IsCancellationRequested);

        h.Session.StartTranslation("again");
        var second = await h.NextCall();
        h.Session.Dispose();
        Assert.True(second.Ct.IsCancellationRequested);
        second.Result.TrySetResult(second.Draft with { JapaneseText = "x" });
        await h.PumpAll();
        Assert.Equal(OutgoingState.Translating, h.Session.State); // frozen; the owner is gone
        h.Session.StartTranslation("ignored after dispose");
        Assert.True(h.Translator.Calls.IsEmpty);
    }
}
