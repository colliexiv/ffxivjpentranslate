using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JpEnChat.Models;
using JpEnChat.Translation;
using Xunit;

namespace JpEnChat.Tests;

public class AnthropicClientTests
{
    private static Configuration AnthropicConfig(string glossary = "") => new()
    {
        Provider = LlmProvider.Anthropic,
        AnthropicModel = "claude-haiku-5-5",
        AnthropicOutgoingModel = "claude-sonnet-5-5",
        AnthropicOutgoingEffort = "medium",
        UserGlossary = glossary,
    };

    private static AnthropicClient Client(HttpMessageHandler handler, string key = "sk-ant-test", bool fallback = true, int timeoutSeconds = 8) =>
        new(() => key, () => timeoutSeconds, () => fallback, new TestLog(), handler);

    private static string Ev(string type, object payload) =>
        "event: " + type + "\ndata: " + JsonSerializer.Serialize(payload);

    private static string MessageStart(string model = "claude-haiku-5-5") => Ev("message_start", new
    {
        type = "message_start",
        message = new
        {
            id = "msg_1",
            type = "message",
            role = "assistant",
            model,
            content = Array.Empty<object>(),
            usage = new { input_tokens = 40, cache_read_input_tokens = 2100, cache_creation_input_tokens = 0, output_tokens = 1 },
        },
    });

    private static string Text(string text) => Ev("content_block_delta", new
    {
        type = "content_block_delta",
        index = 1,
        delta = new { type = "text_delta", text },
    });

    private static string Thinking(string thinking) => Ev("content_block_delta", new
    {
        type = "content_block_delta",
        index = 0,
        delta = new { type = "thinking_delta", thinking },
    });

    private static string MessageDelta(string stopReason, int outputTokens = 12) => Ev("message_delta", new
    {
        type = "message_delta",
        delta = new { stop_reason = stopReason },
        usage = new { output_tokens = outputTokens },
    });

    private static readonly string MessageStop = Ev("message_stop", new { type = "message_stop" });

    private static LlmRequest IncomingRequest(Configuration config, string user = "こんにちは") =>
        new LlmTranslator(config, new NullBackend(), new TestLog())
            .BuildBatchRequest(LlmProvider.Anthropic, [TestUtil.JaLine(user)], Lang.En, null);

    [Fact]
    public void IncomingBodyCachesTheBuiltInPromptOnlyAndSendsNoThinkingOrTemperature()
    {
        var req = IncomingRequest(AnthropicConfig("ノ = o/"));
        using var doc = JsonDocument.Parse(AnthropicClient.SerializeBody(req, stream: true, refusalFallback: true));
        var root = doc.RootElement;

        Assert.Equal("claude-haiku-5-5", root.GetProperty("model").GetString());
        Assert.Equal(4096, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        var system = root.GetProperty("system");
        Assert.Equal(2, system.GetArrayLength());
        Assert.Equal("text", system[0].GetProperty("type").GetString());
        Assert.Equal(Prompts.IncomingSystem, system[0].GetProperty("text").GetString());
        Assert.Equal("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("Player-defined glossary (highest priority; follow these exactly):\nノ = o/", system[1].GetProperty("text").GetString());
        Assert.False(system[1].TryGetProperty("cache_control", out _));
        Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("1: こんにちは", root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("low", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(root.GetProperty("output_config").TryGetProperty("format", out _));
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("fallbacks", out _)); // never for Haiku
        Assert.False(root.TryGetProperty("models", out _));
    }

    [Fact]
    public void BlankGlossaryLeavesOneSystemBlock()
    {
        using var doc = JsonDocument.Parse(AnthropicClient.SerializeBody(IncomingRequest(AnthropicConfig()), stream: true, refusalFallback: false));
        Assert.Equal(1, doc.RootElement.GetProperty("system").GetArrayLength());
    }

    [Fact]
    public void OutgoingBodyPutsEffortAndSchemaInOutputConfigAndFallbackForSonnet()
    {
        var translator = new LlmTranslator(AnthropicConfig(), new NullBackend(), new TestLog());
        var req = translator.BuildOutgoingRequest(LlmProvider.Anthropic, "Style: polite — x\nText: hi");
        using var doc = JsonDocument.Parse(AnthropicClient.SerializeBody(req, stream: true, refusalFallback: true));
        var root = doc.RootElement;

        Assert.Equal("claude-sonnet-5-5", root.GetProperty("model").GetString());
        Assert.Equal(8192, root.GetProperty("max_tokens").GetInt32());
        var outputConfig = root.GetProperty("output_config");
        Assert.Equal("medium", outputConfig.GetProperty("effort").GetString());
        var format = outputConfig.GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.False(format.TryGetProperty("name", out _));
        Assert.False(format.TryGetProperty("strict", out _));
        var schema = format.GetProperty("schema");
        Assert.Equal(["ja", "segments", "back", "style"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(
            ["polite", "casual", "cool", "custom"],
            schema.GetProperty("properties").GetProperty("style").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.False(root.TryGetProperty("response_format", out _));
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());

        using var off = JsonDocument.Parse(AnthropicClient.SerializeBody(req, stream: true, refusalFallback: false));
        Assert.False(off.RootElement.TryGetProperty("fallbacks", out _));

        using var haiku = JsonDocument.Parse(AnthropicClient.SerializeBody(req with { Model = "claude-haiku-5-5" }, stream: true, refusalFallback: true));
        Assert.False(haiku.RootElement.TryGetProperty("fallbacks", out _));

        using var opus = JsonDocument.Parse(AnthropicClient.SerializeBody(req with { Model = "claude-opus-5-5" }, stream: true, refusalFallback: true));
        Assert.Equal("default", opus.RootElement.GetProperty("fallbacks").GetString());
    }

    [Fact]
    public void EmptyEffortOmitsOutputConfig()
    {
        var req = IncomingRequest(AnthropicConfig()) with { Effort = null };
        using var doc = JsonDocument.Parse(AnthropicClient.SerializeBody(req, stream: false, refusalFallback: false));
        Assert.False(doc.RootElement.TryGetProperty("output_config", out _));
        Assert.False(doc.RootElement.TryGetProperty("stream", out _));
    }

    [Fact]
    public async Task StreamYieldsTextDeltasSkipsThinkingAndReportsUsage()
    {
        var handler = FakeHandler.Sse(
            MessageStart(),
            string.Empty,
            Ev("ping", new { type = "ping" }),
            Ev("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = string.Empty } }),
            Thinking("Let me think about 1ボス"),
            Ev("content_block_stop", new { type = "content_block_stop", index = 0 }),
            Text("1: heading to "),
            Text("boss 1"),
            MessageDelta("end_turn", 30),
            MessageStop,
            Text("after stop - ignored"));
        using var client = Client(handler);
        LlmTiming? timing = null;

        var deltas = new List<string>();
        await foreach (var d in client.StreamAsync(IncomingRequest(AnthropicConfig()), CancellationToken.None, t => timing = t))
        {
            deltas.Add(d);
        }

        Assert.Equal(["1: heading to ", "boss 1"], deltas);
        Assert.NotNull(timing);
        Assert.Equal("claude-haiku-5-5", timing!.Model);
        Assert.Equal(2140, timing.PromptTokens);
        Assert.Equal(2100, timing.CachedPromptTokens);
        Assert.Equal(0, timing.CacheWriteTokens);
        Assert.Equal(30, timing.CompletionTokens);
        Assert.Equal("end_turn", timing.FinishReason);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://api.anthropic.com/v1/messages"), req.RequestUri);
        Assert.Equal("sk-ant-test", req.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", req.Headers.GetValues("anthropic-version").Single());
        Assert.Null(req.Headers.Authorization);
        Assert.False(req.Headers.Contains("anthropic-beta"));
        Assert.Equal("application/json", req.Content!.Headers.ContentType!.MediaType);
        Assert.DoesNotContain("sk-ant-test", handler.Bodies.Single());
    }

    [Fact]
    public async Task SonnetRequestSendsTheFallbackBetaHeader()
    {
        var handler = FakeHandler.Sse(MessageStart("claude-sonnet-5-5"), Text("{\"ja\":\"x\"}"), MessageDelta("end_turn"), MessageStop);
        using var client = Client(handler);
        var req = IncomingRequest(AnthropicConfig()) with { Model = "claude-sonnet-5-5" };

        Assert.Equal("{\"ja\":\"x\"}", await client.CompleteAsync(req, CancellationToken.None, null));

        Assert.Equal(AnthropicClient.FallbackBeta, Assert.Single(handler.Requests).Headers.GetValues("anthropic-beta").Single());
        Assert.Contains("\"fallbacks\":\"default\"", handler.Bodies.Single());
    }

    [Fact]
    public async Task RefusalFailsTheRequest()
    {
        var handler = FakeHandler.Sse(MessageStart(), Text("1: par"), MessageDelta("refusal"), MessageStop);
        using var client = Client(handler);
        var translator = new LlmTranslator(AnthropicConfig(), client, new TestLog());

        var ex = await Assert.ThrowsAsync<LlmRefusalException>(() =>
            translator.TranslateBatchAsync([TestUtil.JaLine("一")], Lang.En, null, new ListProgress(), CancellationToken.None));
        Assert.Equal("refused", TranslationPipeline.DescribeError(ex));
    }

    [Fact]
    public async Task ErrorEventMidStreamThrows()
    {
        var handler = FakeHandler.Sse(
            MessageStart(),
            Text("1: a"),
            Ev("error", new { type = "error", error = new { type = "overloaded_error", message = "Overloaded" } }));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<AnthropicException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(IncomingRequest(AnthropicConfig()), CancellationToken.None, null))
            {
            }
        });

        Assert.Equal(529, ex.StatusCode);
        Assert.Equal("overloaded_error", ex.ErrorType);
        Assert.Equal("529 overloaded", TranslationPipeline.DescribeError(ex));
    }

    [Theory]
    [InlineData(429, "rate_limit_error", "Number of request tokens has exceeded your per-minute rate limit", "429 rate limited")]
    [InlineData(401, "authentication_error", "invalid x-api-key", "bad key")]
    [InlineData(400, "invalid_request_error", "max_tokens: Field required", "400: max_tokens: Field required")]
    [InlineData(529, "overloaded_error", "Overloaded", "529 overloaded")]
    public async Task HttpErrorsAreMapped(int status, string type, string message, string described)
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(
            (HttpStatusCode)status,
            JsonSerializer.Serialize(new { type = "error", error = new { type, message } })));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<AnthropicException>(() => client.CompleteAsync(IncomingRequest(AnthropicConfig()), CancellationToken.None, null));

        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(type, ex.ErrorType);
        Assert.Contains(message, ex.Message);
        Assert.Equal(described, TranslationPipeline.DescribeError(ex));
    }

    [Fact]
    public async Task MissingKeyThrowsBeforeSending()
    {
        var handler = FakeHandler.Sse(MessageStop);
        using var client = Client(handler, key: "");
        var ex = await Assert.ThrowsAsync<MissingApiKeyException>(() => client.CompleteAsync(IncomingRequest(AnthropicConfig()), CancellationToken.None, null));
        Assert.Contains("Anthropic", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OutgoingTranslationEndToEndOverTheClaudeApi()
    {
        const string answer = "{\"ja\":\"行こうか。\",\"segments\":[{\"ja\":\"行こうか。\",\"reading\":\"いこうか。\",\"en\":\"shall we go\"}],\"back\":\"Shall we go.\",\"style\":\"cool\"}";
        var handler = FakeHandler.Sse(MessageStart("claude-sonnet-5-5"), Text(answer[..20]), Text(answer[20..]), MessageDelta("end_turn"), MessageStop);
        using var anthropic = Client(handler);
        using var openRouter = TestUtil.Client(FakeHandler.Sse("data: [DONE]"));
        var config = AnthropicConfig();
        var translator = new LlmTranslator(config, new BackendSwitch(() => config.Provider, openRouter, anthropic), new TestLog());
        var context = new TranslationContext
        {
            Environment = new GameEnvironment("Limsa Lominsa", string.Empty, "WHM"),
            Channel = "Party",
        };

        var result = await translator.TranslateOutgoingAsync(
            new OutgoingDraft { EnglishText = "let's go", ChannelPrefix = "/p ", Style = OutgoingStyle.Cool, Context = context },
            CancellationToken.None);

        Assert.Equal("行こうか。", result.JapaneseText);
        Assert.Equal(OutgoingStyle.Cool, result.Style);
        Assert.Equal("Shall we go.", result.BackTranslation);
        using var doc = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal(
            "Context (do not translate): zone=Limsa Lominsa; duty=none; my job=WHM; channel=Party\n"
            + "Style: cool — " + Prompts.CoolStyle + "\nText: let's go",
            doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(Prompts.OutgoingSystem, doc.RootElement.GetProperty("system")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task BackendSwitchFollowsTheConfiguredProvider()
    {
        var anthropicHandler = FakeHandler.Sse(MessageStart(), Text("1: from claude"), MessageDelta("end_turn"), MessageStop);
        var openRouterHandler = FakeHandler.Sse(Sse.Delta("1: from openrouter"), "data: [DONE]");
        using var anthropic = Client(anthropicHandler);
        using var openRouter = TestUtil.Client(openRouterHandler);
        var config = AnthropicConfig();
        var translator = new LlmTranslator(config, new BackendSwitch(() => config.Provider, openRouter, anthropic), new TestLog());

        var first = TestUtil.JaLine("一");
        var progress = new ListProgress();
        await translator.TranslateBatchAsync([first], Lang.En, null, progress, CancellationToken.None);
        Assert.Equal("from claude", progress.TextFor(first.Id));

        config.Provider = LlmProvider.OpenRouter;
        var second = TestUtil.JaLine("二");
        await translator.TranslateBatchAsync([second], Lang.En, null, progress, CancellationToken.None);
        Assert.Equal("from openrouter", progress.TextFor(second.Id));
        Assert.Single(anthropicHandler.Requests);
        Assert.Single(openRouterHandler.Requests);
        Assert.Contains("google/gemini-3.8-flash", openRouterHandler.Bodies.Single());
    }

    /// <summary>Backend that must not be called (request-building tests).</summary>
    private sealed class NullBackend : ILlmBackend
    {
        public IAsyncEnumerable<string> StreamAsync(LlmRequest req, CancellationToken ct, Action<LlmTiming>? onTiming) =>
            throw new InvalidOperationException();

        public Task<string> CompleteAsync(LlmRequest req, CancellationToken ct, Action<LlmTiming>? onTiming) =>
            throw new InvalidOperationException();
    }
}
