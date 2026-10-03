using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JpEnChat.Models;
using JpEnChat.Translation;

namespace JpEnChat.Tests;

internal sealed class TestLog : ILog
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public void Debug(string message) => Messages.Enqueue("DBG " + message);

    public void Information(string message) => Messages.Enqueue("INF " + message);

    public void Warning(string message) => Messages.Enqueue("WRN " + message);

    public void Error(Exception? exception, string message) => Messages.Enqueue("ERR " + message + " " + exception);
}

/// <summary>Records reports synchronously, in order.</summary>
internal sealed class ListProgress : IProgress<(long LineId, string Delta)>
{
    public List<(long LineId, string Delta)> Reports { get; } = [];

    public void Report((long LineId, string Delta) value) => Reports.Add(value);

    public string TextFor(long id)
    {
        var sb = new StringBuilder();
        foreach (var (lineId, delta) in Reports)
        {
            if (lineId == id)
            {
                sb.Append(delta);
            }
        }

        return sb.ToString();
    }
}

/// <summary>HttpMessageHandler returning a canned response and capturing the request.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> respond;

    public FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) => this.respond = respond;

    public List<string> Bodies { get; } = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    public static FakeHandler Sse(params string[] lines) =>
        new((_, _) => SseResponse(lines));

    public static HttpResponseMessage SseResponse(IEnumerable<string> lines)
    {
        var body = string.Join("\n", lines) + "\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))),
        };
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Bodies.Add(body);
        Requests.Add(request);
        return respond(request, body);
    }
}

internal static class Sse
{
    public static string Delta(string content, string model = "google/gemini-3.8-flash") =>
        "data: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "gen-1",
            model,
            provider = "Google",
            choices = new[] { new { index = 0, delta = new { role = "assistant", content } } },
        });
}

internal static class TestUtil
{
    public static ChatLine JaLine(string text, string sender = "Tanaka Taro", string world = "Gaia") => new()
    {
        Original = text,
        OriginalLang = Lang.Ja,
        SenderName = sender,
        SenderWorld = world,
    };

    public static OpenRouterClient Client(HttpMessageHandler handler, int timeoutSeconds = 8, string key = "sk-or-test") =>
        new(() => key, () => timeoutSeconds, new TestLog(), handler);

    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(10);
        }
    }

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jpenchat-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
