using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JpEnChat.Models;
using JpEnChat.Translation;
using Xunit;

namespace JpEnChat.Tests;

public class NumberedLineRouterTests
{
    private static (ListProgress Progress, NumberedLineRouter Router) Make(params long[] ids)
    {
        var p = new ListProgress();
        return (p, new NumberedLineRouter(ids, p));
    }

    [Fact]
    public void RoutesLinesSplitAcrossDeltas()
    {
        var (p, r) = Make(10, 20, 30);
        foreach (var d in new[] { "1", ": Hel", "lo\n2:", " Spread ", "then stack\n", "3: g", "g" })
        {
            r.Push(d);
        }

        r.Complete();
        Assert.Equal("Hello", p.TextFor(10));
        Assert.Equal("Spread then stack", p.TextFor(20));
        Assert.Equal("gg", p.TextFor(30));
        Assert.Empty(r.MissingIds);
    }

    [Fact]
    public void StreamsBeforeNewline()
    {
        var (p, r) = Make(1);
        r.Push("1: Hi");
        Assert.Equal("Hi", p.TextFor(1)); // reported without waiting for the end of the line
    }

    [Fact]
    public void AcceptsFullWidthColonDigitsAndNoSpace()
    {
        var (p, r) = Make(1, 2, 3, 4);
        r.Push("１：first\n2:second\n 3 ： third\n4. fourth");
        r.Complete();
        Assert.Equal("first", p.TextFor(1));
        Assert.Equal("second", p.TextFor(2));
        Assert.Equal("third", p.TextFor(3));
        Assert.Equal("fourth", p.TextFor(4));
    }

    [Fact]
    public void WrappedLineIsAppendedWithSpace()
    {
        var (p, r) = Make(1, 2);
        r.Push("1: Let's pull the first\nboss now\n\n2: ok\n");
        r.Complete();
        Assert.Equal("Let's pull the first boss now", p.TextFor(1));
        Assert.Equal("ok", p.TextFor(2));
    }

    [Fact]
    public void DropsPreambleFencesAndExtraLines()
    {
        var (p, r) = Make(1, 2);
        r.Push("Here you go:\n```\n1: one\n2: two\n3: hallucinated\n1: duplicate\n```");
        r.Complete();
        Assert.Equal("one", p.TextFor(1));
        Assert.Equal("two", p.TextFor(2));
    }

    [Fact]
    public void ContentStartingWithNumberIsNotAPrefix()
    {
        var (p, r) = Make(1);
        r.Push("1: 100% sure\n");
        r.Complete();
        Assert.Equal("100% sure", p.TextFor(1));
    }

    [Fact]
    public void MissingLinesAreReported()
    {
        var (p, r) = Make(5, 6, 7);
        r.Push("1: a\n3:   \n");
        r.Complete();
        Assert.Equal("a", p.TextFor(5));
        Assert.Equal([6L, 7L], r.MissingIds);
    }

    [Fact]
    public void SingleLineWithoutPrefixIsAccepted()
    {
        var (p, r) = Make(9);
        r.Push("Nice to meet ");
        r.Push("you!");
        r.Complete();
        Assert.Equal("Nice to meet you!", p.TextFor(9));
        Assert.Empty(r.MissingIds);
    }

    [Fact]
    public void UnprefixedAnswerForMultiLineBatchIsMissing()
    {
        var (_, r) = Make(1, 2);
        r.Push("Hello\nWorld");
        r.Complete();
        Assert.Equal([1L, 2L], r.MissingIds);
    }
}

public class LlmTranslatorTests
{
    private static Configuration Config() => new()
    {
        Model = "google/gemini-3.8-flash",
        FallbackModels = ["google/gemini-3.5-flash-lite"],
        ReasoningEffort = "low",
        OutgoingModel = "google/gemini-3.8-flash",
    };

    [Fact]
    public void SystemPromptIsLongAndStable()
    {
        // ~1k tokens is the provider prompt-caching threshold; Japanese-heavy text is >1 token per 3 bytes.
        Assert.True(Encoding.UTF8.GetByteCount(Prompts.IncomingSystem) > 4000);
        Assert.Contains("散開", Prompts.IncomingSystem);
    }

    [Fact]
    public void BuildsNumberedUserContent()
    {
        var lines = new[] { TestUtil.JaLine("よろしく"), TestUtil.JaLine("1ボス\n行きます") };
        Assert.Equal("1: よろしく\n2: 1ボス 行きます", LlmTranslator.BuildBatchUserContent(lines, Lang.En));
        Assert.StartsWith("Target: Japanese\n1: ", LlmTranslator.BuildBatchUserContent(lines, Lang.Ja));
    }

    [Fact]
    public async Task BatchStreamsToLineIds()
    {
        var handler = FakeHandler.Sse(Sse.Delta("1: Hi, let's"), Sse.Delta(" do this\n2: First time"), "data: [DONE]");
        using var client = TestUtil.Client(handler);
        var translator = new LlmTranslator(Config(), client, new TestLog());
        var lines = new[] { TestUtil.JaLine("よろしくお願いします"), TestUtil.JaLine("初見です") };
        var progress = new ListProgress();

        await translator.TranslateBatchAsync(lines, Lang.En, null, progress, CancellationToken.None);

        Assert.Equal("Hi, let's do this", progress.TextFor(lines[0].Id));
        Assert.Equal("First time", progress.TextFor(lines[1].Id));

        using var doc = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal(Prompts.IncomingSystem, doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(0.2, doc.RootElement.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task BatchWithMissingLineThrowsAfterFlushing()
    {
        var handler = FakeHandler.Sse(Sse.Delta("1: only one"), "data: [DONE]");
        using var client = TestUtil.Client(handler);
        var translator = new LlmTranslator(Config(), client, new TestLog());
        var lines = new[] { TestUtil.JaLine("一"), TestUtil.JaLine("二") };
        var progress = new ListProgress();

        var ex = await Assert.ThrowsAsync<IncompleteBatchException>(
            () => translator.TranslateBatchAsync(lines, Lang.En, null, progress, CancellationToken.None));

        Assert.Equal([lines[1].Id], ex.MissingIds);
        Assert.Equal("only one", progress.TextFor(lines[0].Id));
    }

    [Fact]
    public async Task OutgoingSendsStrictSchemaAndParses()
    {
        const string answer = """
            {"ja":"1ボスいきましょう","segments":[{"ja":"1ボス","reading":"いちぼす","en":"first boss"},{"ja":"いきましょう","reading":"いきましょう","en":"let's go"}],"back":"Let's go to the first boss.","style":"polite"}
            """;
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.OK, ChatCompletion(answer)));
        using var client = TestUtil.Client(handler);
        var translator = new LlmTranslator(Config(), client, new TestLog());
        var draft = new OutgoingDraft { EnglishText = "let's pull the first boss", ChannelPrefix = "/p ", Style = OutgoingStyle.Polite };

        var result = await translator.TranslateOutgoingAsync(draft, CancellationToken.None);

        Assert.Equal("1ボスいきましょう", result.JapaneseText);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(new Segment("1ボス", "いちぼす", "first boss"), result.Segments[0]);
        Assert.Equal("Let's go to the first boss.", result.BackTranslation);
        Assert.Equal("/p 1ボスいきましょう", result.ToChatCommand());

        using var doc = JsonDocument.Parse(handler.Bodies.Single());
        var root = doc.RootElement;
        Assert.Equal(0.3, root.GetProperty("temperature").GetDouble());
        Assert.True(root.GetProperty("provider").GetProperty("require_parameters").GetBoolean());
        var format = root.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        var props = format.GetProperty("json_schema").GetProperty("schema").GetProperty("properties");
        Assert.Equal(["polite", "casual", "cool", "custom"], props.GetProperty("style").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(
            "Style: polite — " + Prompts.PoliteStyle + "\nText: let's pull the first boss",
            root.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.False(root.TryGetProperty("models", out _));
    }

    [Fact]
    public async Task OutgoingRetriesOnceWhenTooLong()
    {
        var tooLong = new string('あ', 200); // 600 bytes
        var calls = 0;
        var handler = new FakeHandler((_, _) =>
        {
            calls++;
            var ja = calls == 1 ? tooLong : "短い";
            return FakeHandler.Json(HttpStatusCode.OK, ChatCompletion($"{{\"ja\":\"{ja}\",\"segments\":[],\"back\":\"b\",\"style\":\"casual\"}}"));
        });
        using var client = TestUtil.Client(handler);
        var translator = new LlmTranslator(Config(), client, new TestLog());

        var result = await translator.TranslateOutgoingAsync(
            new OutgoingDraft { EnglishText = "long text", ChannelPrefix = "/p ", Style = OutgoingStyle.Casual }, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal("短い", result.JapaneseText);
        Assert.Equal(OutgoingStyle.Casual, result.Style);
        Assert.Contains("shorter version", handler.Bodies[1]);
    }

    [Fact]
    public async Task OutgoingReturnsOverLongResultAfterOneRetry()
    {
        var tooLong = new string('あ', 200);
        var handler = new FakeHandler((_, _) => FakeHandler.Json(
            HttpStatusCode.OK, ChatCompletion($"{{\"ja\":\"{tooLong}\",\"segments\":[],\"back\":\"b\",\"style\":\"polite\"}}")));
        using var client = TestUtil.Client(handler);
        var translator = new LlmTranslator(Config(), client, new TestLog());

        var result = await translator.TranslateOutgoingAsync(new OutgoingDraft { EnglishText = "x" }, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(tooLong, result.JapaneseText);
    }

    [Fact]
    public void ParseOutgoingToleratesFenceProseAndBadStyle()
    {
        var (ja, segments, back, style) = LlmTranslator.ParseOutgoing(
            "```json\n{\"ja\":\"おつ\",\"segments\":[{\"ja\":\"おつ\",\"reading\":\"おつ\",\"en\":\"gg\"}],\"back\":\"gg\",\"style\":\"formal\"}\n```",
            OutgoingStyle.Casual);
        Assert.Equal("おつ", ja);
        Assert.Single(segments);
        Assert.Equal("gg", back);
        Assert.Equal(OutgoingStyle.Casual, style);
        Assert.Throws<LlmException>(() => LlmTranslator.ParseOutgoing("not json", OutgoingStyle.Polite));
        Assert.Throws<LlmException>(() => LlmTranslator.ParseOutgoing("{\"ja\":\"\"}", OutgoingStyle.Polite));

        var (proseJa, _, _, proseStyle) = LlmTranslator.ParseOutgoing(
            "Here is the JSON:\n{\"ja\":\"行こうか。\",\"segments\":[],\"back\":\"Shall we go.\",\"style\":\"cool\"}\nDone.",
            OutgoingStyle.Polite);
        Assert.Equal("行こうか。", proseJa);
        Assert.Equal(OutgoingStyle.Cool, proseStyle);
    }

    private static string ChatCompletion(string content) =>
        JsonSerializer.Serialize(new
        {
            model = "google/gemini-3.8-flash",
            choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } },
        });
}
