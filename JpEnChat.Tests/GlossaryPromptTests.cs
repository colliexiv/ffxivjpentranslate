using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JpEnChat.Models;
using JpEnChat.Translation;
using Xunit;

namespace JpEnChat.Tests;

public class GlossaryPromptTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n \r\n\t")]
    public void BlankGlossaryLeavesPromptsUnchanged(string? glossary)
    {
        Assert.Same(Prompts.IncomingSystem, Prompts.Incoming(glossary));
        Assert.Same(Prompts.OutgoingSystem, Prompts.Outgoing(glossary));
    }

    [Fact]
    public void GlossaryIsAppendedAfterTheUnchangedPrefix()
    {
        const string glossary = "  ノ = o/ (raised hand)  \r\n\n\n  CT = cooldown\n";

        var incoming = Prompts.Incoming(glossary);
        Assert.StartsWith(Prompts.IncomingSystem, incoming, System.StringComparison.Ordinal);
        Assert.Equal(
            Prompts.IncomingSystem
            + "\n\nPlayer-defined glossary (highest priority; follow these exactly):\nノ = o/ (raised hand)\nCT = cooldown",
            incoming);

        var outgoing = Prompts.Outgoing(glossary);
        Assert.StartsWith(Prompts.OutgoingSystem, outgoing, System.StringComparison.Ordinal);
        Assert.EndsWith(
            "\n\nPlayer-defined glossary (use these preferred renderings when relevant):\nノ = o/ (raised hand)\nCT = cooldown",
            outgoing,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void GlossaryIsCappedAtLineBoundary()
    {
        var glossary = string.Join("\n", Enumerable.Repeat("abcdefghij", 1000)); // 10 chars per line
        var cleaned = Prompts.CleanGlossary(glossary);
        Assert.True(cleaned.Length <= Prompts.MaxUserGlossaryChars);
        Assert.Equal(363, cleaned.Split('\n').Length); // 363 × 10 + 362 newlines = 3992
        Assert.All(cleaned.Split('\n'), l => Assert.Equal("abcdefghij", l));
        Assert.EndsWith(cleaned, Prompts.Incoming(glossary), System.StringComparison.Ordinal);

        var oneLongLine = new string('x', 5000);
        Assert.Equal(Prompts.MaxUserGlossaryChars, Prompts.CleanGlossary(oneLongLine).Length);
    }

    [Fact]
    public void BuiltInPromptsCoverChatConventions()
    {
        Assert.Contains("Never output slash commands", Prompts.IncomingSystem);
        Assert.Contains("ノ = o/", Prompts.IncomingSystem);
        Assert.Contains("ノシ = o/", Prompts.IncomingSystem);
        Assert.Contains("Never output slash commands", Prompts.OutgoingSystem);
    }

    [Fact]
    public async Task RequestsCarryTheGlossaryInTheSystemMessage()
    {
        var config = new Configuration { UserGlossary = "ノ = o/ (raised hand)" };
        var handler = FakeHandler.Sse(Sse.Delta("1: o/"), "data: [DONE]");
        using var client = TestUtil.Client(handler);
        var translator = new OpenRouterTranslator(config, client, new TestLog());

        await translator.TranslateBatchAsync([TestUtil.JaLine("ノ")], Lang.En, new ListProgress(), CancellationToken.None);

        using (var doc = JsonDocument.Parse(handler.Bodies.Single()))
        {
            var system = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Assert.Equal(Prompts.Incoming(config.UserGlossary), system);
            Assert.EndsWith("follow these exactly):\nノ = o/ (raised hand)", system, System.StringComparison.Ordinal);
        }

        const string answer = "{\"ja\":\"ノ\",\"segments\":[],\"back\":\"o/\",\"register\":\"casual\"}";
        var outHandler = new FakeHandler((_, _) => FakeHandler.Json(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = answer } } } })));
        using var outClient = TestUtil.Client(outHandler);
        var outTranslator = new OpenRouterTranslator(config, outClient, new TestLog());
        await outTranslator.TranslateOutgoingAsync(new OutgoingDraft { EnglishText = "o/", Register = Registers.Casual }, CancellationToken.None);

        using var outDoc = JsonDocument.Parse(outHandler.Bodies.Single());
        Assert.Equal(
            Prompts.Outgoing(config.UserGlossary),
            outDoc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }
}
