using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JpEnChat.Translation;
using Xunit;

namespace JpEnChat.Tests;

public class OpenRouterClientTests
{
    private static readonly ChatRequest Request = new()
    {
        Model = "google/gemini-3.8-flash",
        FallbackModels = ["google/gemini-3.5-flash-lite"],
        SystemPrompt = "sys",
        UserContent = "1: こんにちは",
        Temperature = 0.2,
        MaxTokens = 300,
        ReasoningEffort = "low",
    };

    private static async Task<List<string>> Collect(OpenRouterClient client, Action<LlmTiming>? onTiming = null)
    {
        var list = new List<string>();
        await foreach (var s in client.StreamChatAsync(Request, CancellationToken.None, onTiming))
        {
            list.Add(s);
        }

        return list;
    }

    [Fact]
    public async Task StreamsDeltasSkippingCommentsUntilDone()
    {
        var handler = FakeHandler.Sse(
            ": OPENROUTER PROCESSING",
            string.Empty,
            ": OPENROUTER PROCESSING",
            Sse.Delta("1: Hel"),
            string.Empty,
            Sse.Delta("lo"),
            "data: {\"choices\":[{\"delta\":{\"content\":\"\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1200,\"completion_tokens\":5,\"prompt_tokens_details\":{\"cached_tokens\":1024}}}",
            "data: [DONE]",
            Sse.Delta("after done - ignored"));
        using var client = TestUtil.Client(handler);
        LlmTiming? timing = null;

        var deltas = await Collect(client, t => timing = t);

        Assert.Equal(["1: Hel", "lo"], deltas);
        Assert.NotNull(timing);
        Assert.Equal("google/gemini-3.8-flash", timing!.Model);
        Assert.Equal("Google", timing.Provider);
        Assert.NotNull(timing.FirstTokenMs);
        Assert.True(timing.TotalMs >= timing.FirstTokenMs);
        Assert.Equal(1200, timing.PromptTokens);
        Assert.Equal(1024, timing.CachedPromptTokens);
        Assert.Equal("stop", timing.FinishReason);
    }

    [Fact]
    public async Task SendsHeadersAndExpectedBody()
    {
        var handler = FakeHandler.Sse("data: [DONE]");
        using var client = TestUtil.Client(handler, key: "sk-or-secret");
        await Collect(client);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://openrouter.ai/api/v1/chat/completions"), req.RequestUri);
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("sk-or-secret", req.Headers.Authorization.Parameter);
        Assert.Equal("https://github.com/colliexiv/ffxivjpentranslate", req.Headers.GetValues("HTTP-Referer").Single());
        Assert.Equal("JP/EN Chat", req.Headers.GetValues("X-Title").Single());

        using var doc = JsonDocument.Parse(handler.Bodies.Single());
        var root = doc.RootElement;
        Assert.Equal("google/gemini-3.8-flash", root.GetProperty("model").GetString());
        Assert.Equal(["google/gemini-3.8-flash", "google/gemini-3.5-flash-lite"], root.GetProperty("models").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("1: こんにちは", root.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble());
        Assert.Equal(300, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("low", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.True(root.GetProperty("reasoning").GetProperty("exclude").GetBoolean());
        var provider = root.GetProperty("provider");
        Assert.Equal("latency", provider.GetProperty("sort").GetString());
        Assert.True(provider.GetProperty("allow_fallbacks").GetBoolean());
        Assert.Equal("deny", provider.GetProperty("data_collection").GetString());
        Assert.False(provider.TryGetProperty("require_parameters", out _));
        Assert.False(root.TryGetProperty("response_format", out _));
        Assert.DoesNotContain("sk-or-secret", handler.Bodies.Single());
        Assert.Contains("1: こんにちは", handler.Bodies.Single()); // UTF-8, not \uXXXX escapes
    }

    [Fact]
    public void BodyOmitsNullsAndReasoningWithoutEffort()
    {
        var json = OpenRouterClient.SerializeBody(Request with { ReasoningEffort = null, FallbackModels = [], Temperature = null }, stream: false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("reasoning", out _));
        Assert.False(root.TryGetProperty("models", out _));
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("stream", out _));
        Assert.DoesNotContain("null", json);
    }

    [Fact]
    public async Task MidStreamErrorChunkThrows()
    {
        var handler = FakeHandler.Sse(
            Sse.Delta("1: par"),
            "data: {\"id\":\"x\",\"object\":\"chat.completion.chunk\",\"error\":{\"code\":502,\"message\":\"Provider disconnected\"},\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\"},\"finish_reason\":\"error\"}]}",
            "data: [DONE]");
        using var client = TestUtil.Client(handler);
        var seen = new List<string>();

        var ex = await Assert.ThrowsAsync<OpenRouterException>(async () =>
        {
            await foreach (var d in client.StreamChatAsync(Request))
            {
                seen.Add(d);
            }
        });

        Assert.Equal(["1: par"], seen);
        Assert.Equal(502, ex.StatusCode);
        Assert.Contains("Provider disconnected", ex.Message);
    }

    [Fact]
    public async Task FinishReasonErrorWithoutErrorObjectThrows()
    {
        var handler = FakeHandler.Sse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"\"},\"finish_reason\":\"error\"}]}");
        using var client = TestUtil.Client(handler);
        var ex = await Assert.ThrowsAsync<OpenRouterException>(() => Collect(client));
        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public async Task RateLimitStatusIsParsed()
    {
        var handler = new FakeHandler((_, _) =>
        {
            var r = FakeHandler.Json(
                (HttpStatusCode)429,
                "{\"error\":{\"code\":429,\"message\":\"Rate limit exceeded: free-models-per-min\",\"metadata\":{\"limit_source\":\"key\"}}}");
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return r;
        });
        using var client = TestUtil.Client(handler);

        var ex = await Assert.ThrowsAsync<OpenRouterException>(() => Collect(client));

        Assert.Equal(429, ex.StatusCode);
        Assert.Contains("Rate limit exceeded", ex.Message);
        Assert.Equal("key", ex.LimitSource);
        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
        Assert.Equal("429 rate limited", TranslationPipeline.DescribeError(ex));
    }

    [Fact]
    public async Task NonJsonErrorBodyFallsBackToStatus()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>bad gateway</html>"),
        });
        using var client = TestUtil.Client(handler);
        var ex = await Assert.ThrowsAsync<OpenRouterException>(() => Collect(client));
        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task MissingKeyThrowsBeforeSending()
    {
        var handler = FakeHandler.Sse("data: [DONE]");
        using var client = TestUtil.Client(handler, key: " ");
        await Assert.ThrowsAsync<MissingApiKeyException>(() => Collect(client));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StalledStreamTimesOut()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream()),
        });
        using var client = TestUtil.Client(handler, timeoutSeconds: 1);
        await Assert.ThrowsAsync<TimeoutException>(() => Collect(client));
    }

    [Fact]
    public async Task CallerCancellationIsNotATimeout()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream()),
        });
        using var client = TestUtil.Client(handler, timeoutSeconds: 30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.StreamChatAsync(Request, cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task CompleteReturnsMessageContent()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(
            HttpStatusCode.OK,
            "{\"model\":\"m\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"ja\\\":\\\"x\\\"}\"},\"finish_reason\":\"stop\"}]}"));
        using var client = TestUtil.Client(handler);
        var content = await client.CompleteAsync(Request);
        Assert.Equal("{\"ja\":\"x\"}", content);
        using var doc = JsonDocument.Parse(handler.Bodies.Single());
        Assert.False(doc.RootElement.TryGetProperty("stream", out _));
    }

    [Fact]
    public async Task CompleteThrowsOnErrorBodyWith200()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(
            HttpStatusCode.OK, "{\"error\":{\"code\":402,\"message\":\"Insufficient credits\"}}"));
        using var client = TestUtil.Client(handler);
        var ex = await Assert.ThrowsAsync<OpenRouterException>(() => client.CompleteAsync(Request));
        Assert.Equal(402, ex.StatusCode);
        Assert.Equal("402 out of credits", TranslationPipeline.DescribeError(ex));
    }

    /// <summary>A response body that never produces data until cancelled.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
