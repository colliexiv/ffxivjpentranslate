using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dalamud.Game.Text;
using JpEnChat.Models;
using JpEnChat.Translation;
using Newtonsoft.Json;
using Xunit;

namespace JpEnChat.Tests;

public class ContextStyleAndConfigTests
{
    private static ChatLine Line(XivChatType kind, string sender, string text, string? translation = null, bool own = false, string world = "Gaia") => new()
    {
        Kind = kind,
        SenderName = sender,
        SenderWorld = world,
        Original = text,
        OriginalLang = Lang.Ja,
        Translation = translation ?? string.Empty,
        Status = translation is null ? TranslationStatus.Pending : TranslationStatus.Done,
        IsOwn = own,
    };

    private static TranslationContext SampleContext() => new()
    {
        Environment = new GameEnvironment("Kugane", "the Sunken Temple of Qarn", "WHM"),
        Channel = "Party",
        RecentLines =
        [
            new ContextLine("Tanaka", "1ボス行きます", "heading to boss 1"),
            new ContextLine("Sato", "了解", null),
        ],
    };

    [Fact]
    public void IncomingUserContentPutsContextBeforeTheNumberedLines()
    {
        var lines = new[] { TestUtil.JaLine("散開→頭割り"), TestUtil.JaLine("よろしく\nです") };

        var content = LlmTranslator.BuildBatchUserContent(lines, Lang.En, SampleContext());

        Assert.Equal(
            "Context (do not translate): zone=Kugane; duty=the Sunken Temple of Qarn; my job=WHM; channel=Party\n"
            + "Recent lines:\n"
            + "Tanaka: 1ボス行きます → heading to boss 1\n"
            + "Sato: 了解\n"
            + "Translate:\n"
            + "1: 散開→頭割り\n"
            + "2: よろしく です",
            content);
        Assert.Equal("1: 散開→頭割り\n2: よろしく です", LlmTranslator.BuildBatchUserContent(lines, Lang.En)); // no context: unchanged
    }

    [Fact]
    public void ContextWithoutRecentLinesAndUnknownValues()
    {
        var context = new TranslationContext { Channel = "Say" };
        Assert.Equal("Context (do not translate): zone=unknown; duty=none; my job=unknown; channel=Say\n", context.Format());
        Assert.Equal(
            "Target: Japanese\nContext (do not translate): zone=unknown; duty=none; my job=unknown; channel=Say\nTranslate:\n1: hi",
            LlmTranslator.BuildBatchUserContent([TestUtil.JaLine("hi")], Lang.Ja, context));
    }

    [Fact]
    public void ContextLinesAreOneLineAndTruncated()
    {
        var formatted = TranslationContext.FormatLine(new ContextLine("Tanaka", new string('あ', 200), "x"));
        Assert.Equal(TranslationContext.MaxLineChars, formatted.Length);
        Assert.EndsWith("…", formatted, StringComparison.Ordinal);
        Assert.Equal("Me: a b → c", TranslationContext.FormatLine(new ContextLine("Me", "a\nb", "c")));
    }

    [Fact]
    public void RecentLinesKeepTheSameChannelBeforeTheBatchOldestFirst()
    {
        var log = new ChatLog(() => 1000);
        var p1 = Line(XivChatType.Party, "Tanaka", "1ボス行きます", "heading to boss 1");
        var fc = Line(XivChatType.FreeCompany, "Suzuki", "FCの話");
        var p2 = Line(XivChatType.CrossParty, "Sato", "了解");
        var mine = new ChatLine
        {
            Kind = XivChatType.Party,
            SenderName = "My Name",
            Original = "ok",
            OriginalLang = Lang.En,
            Translation = "了解です",
            Status = TranslationStatus.Done,
            IsOwn = true,
            IsSentByPlugin = true,
        };
        var first = Line(XivChatType.Party, "Tanaka", "散開");
        var later = Line(XivChatType.Party, "Tanaka", "頭割り");
        foreach (var l in new[] { p1, fc, p2, mine, first, later })
        {
            log.Add(l);
        }

        var builder = new TranslationContextBuilder(log, () => 6, () => new GameEnvironment("Z", string.Empty, "SCH"), k => k.ToString());
        var context = builder.ForIncoming(first);

        Assert.Equal("Party", context.Channel);
        Assert.Equal("SCH", context.Environment.Job);
        Assert.Equal(
            ["Tanaka: 1ボス行きます → heading to boss 1", "Sato: 了解", "Me: ok → 了解です"],
            context.RecentLines.Select(TranslationContext.FormatLine).ToArray());

        var two = new TranslationContextBuilder(log, () => 2, () => GameEnvironment.Unknown, k => k.ToString()).ForIncoming(first);
        Assert.Equal(["Sato", "Me"], two.RecentLines.Select(l => l.Sender).ToArray());

        var none = new TranslationContextBuilder(log, () => 0, () => GameEnvironment.Unknown, k => k.ToString()).ForIncoming(first);
        Assert.Empty(none.RecentLines);
    }

    [Fact]
    public void TellContextIsTheSamePartnerOnly()
    {
        var log = new ChatLog(() => 1000);
        var fromA = Line(XivChatType.TellIncoming, "Alice Aa", "こんにちは");
        var fromB = Line(XivChatType.TellIncoming, "Bob Bb", "やあ");
        var toA = Line(XivChatType.TellOutgoing, "Alice Aa", "hello", "こんにちは", own: true);
        var party = Line(XivChatType.Party, "Alice Aa", "パーティ");
        foreach (var l in new[] { fromA, fromB, toA, party })
        {
            log.Add(l);
        }

        var builder = new TranslationContextBuilder(log, () => 6, () => GameEnvironment.Unknown, k => k.ToString());
        var outgoing = builder.ForOutgoing(XivChatType.TellOutgoing, "Alice Aa@Gaia");
        Assert.Equal("Tell", outgoing.Channel);
        Assert.Equal(["Alice Aa: こんにちは", "Me: hello → こんにちは"], outgoing.RecentLines.Select(TranslationContext.FormatLine).ToArray());

        var next = Line(XivChatType.TellIncoming, "Bob Bb", "またね");
        log.Add(next);
        Assert.Equal(["Bob Bb: やあ"], builder.ForIncoming(next).RecentLines.Select(TranslationContext.FormatLine).ToArray());
    }

    [Fact]
    public void StyleSectionsAreAssembledInTheUserContent()
    {
        Assert.Equal("Style: polite — " + Prompts.PoliteStyle, LlmTranslator.StyleSection(OutgoingStyle.Polite, null));
        Assert.Equal("Style: casual — " + Prompts.CasualStyle, LlmTranslator.StyleSection(OutgoingStyle.Casual, null));
        Assert.Equal("Style: cool — " + Prompts.CoolStyle, LlmTranslator.StyleSection(OutgoingStyle.Cool, "ignored"));
        Assert.Equal(
            "Style: custom — " + Prompts.CustomStyleInstruction("speak like a cheerful Lalafell"),
            LlmTranslator.StyleSection(OutgoingStyle.Custom, "  speak like a\ncheerful Lalafell "));
        Assert.Equal("Style: polite — " + Prompts.PoliteStyle, LlmTranslator.StyleSection(OutgoingStyle.Custom, "   "));
        Assert.Contains("行こうか。", Prompts.CoolStyle);
        Assert.Equal(LlmTranslator.MaxCustomStyleChars, LlmTranslator.CleanCustomStyle(new string('x', 900)).Length);

        Assert.Equal(
            SampleContext().Format() + "Style: casual — " + Prompts.CasualStyle + "\nText: o/ see you",
            LlmTranslator.BuildOutgoingUserContent(" o/ see you\n", OutgoingStyle.Casual, null, SampleContext()));
    }

    [Fact]
    public void StylesRoundTripTheirWireNames()
    {
        foreach (var style in Styles.All)
        {
            Assert.Equal(style, Styles.FromWire(Styles.ToWire(style)));
        }

        Assert.Null(Styles.FromWire("formal"));
        Assert.Equal(OutgoingStyle.Polite, Styles.Normalize((OutgoingStyle)42));
    }

    [Theory]
    [InlineData("casual", OutgoingStyle.Casual)]
    [InlineData("polite", OutgoingStyle.Polite)]
    [InlineData(null, OutgoingStyle.Polite)]
    public void MigratesSchemaOneRegisterToStyle(string? register, OutgoingStyle expected)
    {
        var json = "{\"Version\":1,\"Model\":\"google/gemini-3.8-flash\""
            + (register is null ? string.Empty : $",\"DefaultRegister\":\"{register}\"") + "}";
        var config = JsonConvert.DeserializeObject<Configuration>(json)!;

        config.Migrate();

        Assert.Equal(expected, config.DefaultStyle);
        Assert.Equal(2, config.Version);
        Assert.Equal(LlmProvider.OpenRouter, config.Provider); // existing users stay on OpenRouter
        Assert.Equal("claude-haiku-5-5", config.AnthropicModel);
        Assert.Equal("claude-sonnet-5-5", config.AnthropicOutgoingModel);
        Assert.Equal("medium", config.AnthropicOutgoingEffort);
        Assert.Equal(6, config.ContextLines);
        Assert.True(config.AnthropicRefusalFallback);
        Assert.DoesNotContain("DefaultRegister", JsonConvert.SerializeObject(config));
    }

    [Fact]
    public void MigrationDoesNotTouchAVersionTwoStyle()
    {
        var config = JsonConvert.DeserializeObject<Configuration>(
            "{\"Version\":2,\"DefaultStyle\":2,\"Provider\":1,\"ContextLines\":99,\"AnthropicOutgoingEffort\":\"max\"}")!;
        config.Migrate();
        Assert.Equal(OutgoingStyle.Cool, config.DefaultStyle);
        Assert.Equal(LlmProvider.Anthropic, config.Provider);
        Assert.Equal(TranslationContext.MaxContextLines, config.ContextLines);
        Assert.Equal("medium", config.AnthropicOutgoingEffort);
    }
}

public class OpenRouterBodyUnchangedTests
{
    private static Configuration Config(string glossary) => new()
    {
        Model = "google/gemini-3.8-flash",
        FallbackModels = ["google/gemini-3.5-flash-lite"],
        ReasoningEffort = "low",
        OutgoingModel = "google/gemini-3.8-flash",
        UserGlossary = glossary,
    };

    [Theory]
    [InlineData("")]
    [InlineData("ノ = o/ (raised hand)")]
    public void IncomingBodyIsByteIdenticalToTheV04Request(string glossary)
    {
        var config = Config(glossary);
        var lines = new[] { TestUtil.JaLine("よろしくお願いします"), TestUtil.JaLine("初見です") };

        // What 0.4.0's OpenRouterTranslator built for the same input.
        var legacy = new ChatRequest
        {
            Model = config.Model,
            FallbackModels = config.FallbackModels.ToArray(),
            SystemPrompt = Prompts.Incoming(config.UserGlossary),
            UserContent = "1: よろしくお願いします\n2: 初見です",
            Temperature = 0.2,
            MaxTokens = LlmTranslator.BatchMaxTokens(lines),
            ReasoningEffort = "low",
            ExcludeReasoning = true,
            ProviderSort = "latency",
        };

        var request = new LlmTranslator(config, new OpenRouterClientStub(), new TestLog())
            .BuildBatchRequest(LlmProvider.OpenRouter, lines, Lang.En, null);

        Assert.Equal(
            OpenRouterClient.SerializeBody(legacy, stream: true),
            OpenRouterClient.SerializeBody(OpenRouterClient.ToChatRequest(request), stream: true));
    }

    [Fact]
    public void OutgoingBodyKeepsTheV04ShapeWithTheStyleSchema()
    {
        var config = Config(string.Empty);
        var request = new LlmTranslator(config, new OpenRouterClientStub(), new TestLog())
            .BuildOutgoingRequest(LlmProvider.OpenRouter, "Style: polite — x\nText: hi");
        var json = OpenRouterClient.SerializeBody(OpenRouterClient.ToChatRequest(request), stream: false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(
            ["model", "messages", "temperature", "max_tokens", "reasoning", "provider", "response_format"],
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(Prompts.OutgoingSystem, root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(0.3, root.GetProperty("temperature").GetDouble());
        Assert.Equal(4096, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("{\"effort\":\"low\",\"exclude\":true}", root.GetProperty("reasoning").GetRawText());
        Assert.Equal(
            "{\"sort\":\"latency\",\"allow_fallbacks\":true,\"data_collection\":\"deny\",\"require_parameters\":true}",
            root.GetProperty("provider").GetRawText());
        var format = root.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("outgoing", format.GetProperty("json_schema").GetProperty("name").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal(
            LlmTranslator.OutgoingSchema().Schema.ToJsonString(),
            format.GetProperty("json_schema").GetProperty("schema").GetRawText());
    }

    /// <summary>Unused backend; these tests only build requests.</summary>
    private sealed class OpenRouterClientStub : ILlmBackend
    {
        public IAsyncEnumerable<string> StreamAsync(LlmRequest req, System.Threading.CancellationToken ct, Action<LlmTiming>? onTiming) =>
            throw new InvalidOperationException();

        public System.Threading.Tasks.Task<string> CompleteAsync(LlmRequest req, System.Threading.CancellationToken ct, Action<LlmTiming>? onTiming) =>
            throw new InvalidOperationException();
    }
}
