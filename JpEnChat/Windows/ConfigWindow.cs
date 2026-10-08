using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using JpEnChat.Models;
using JpEnChat.Translation;
using JpEnChat.Ui;

namespace JpEnChat.Windows;

/// <summary>
/// Settings window: General, Translation, Vanilla chat, Channels, Translations, Glossary and Keys tabs (PLAN §5, §9, §11, §12).
/// </summary>
/// <remarks>
/// Toggles and combos save immediately. Sliders apply live (so the font size previews while dragging) and save when
/// released. Free-text and numeric fields apply and save when the field loses focus, so a half-typed value (e.g. a
/// log limit of "5") never takes effect. Every save raises <see cref="Changed"/>.
/// API keys are decrypted into buffers only while the window is open and wiped on close.
/// The Translations tab edits the translation cache directly (fixed and cached entries); every edit is followed by a
/// background cache save. The Translations and Glossary tabs use the Axis font, so Japanese renders as in the log.
/// </remarks>
public sealed class ConfigWindow : Window, IDisposable
{
    private const int KeyMaxBytes = 256;
    private const int ModelMaxBytes = 128;

    private const int CustomStyleMaxBytes = LlmTranslator.MaxCustomStyleChars * 3;

    // Claude list prices per million tokens (input/output), shown next to the presets.
    private const string HaikuPrice = "$0.10/$0.50 per MTok";
    private const string SonnetPrice = "$2/$10 per MTok";
    private const string OpusPrice = "$4/$20 per MTok";

    private static readonly ModelPreset[] OpenRouterPresets =
    [
        new("google/gemini-3.8-flash"),
        new("google/gemini-3.5-flash-lite"),
        new("google/gemini-2.5-flash-lite"),
        new("openai/gpt-4.1-mini"),
        new("anthropic/claude-haiku-4.5", "$1/$5 per MTok"),
        new("anthropic/claude-haiku-5.5", HaikuPrice),
        new("anthropic/claude-sonnet-5.5", SonnetPrice),
        new("anthropic/claude-opus-5.5", OpusPrice),
    ];

    private static readonly ModelPreset[] AnthropicPresets =
    [
        new("claude-haiku-5-5", HaikuPrice),
        new("claude-sonnet-5-5", SonnetPrice),
        new("claude-opus-5-5", OpusPrice),
    ];

    private static readonly LlmProvider[] ProviderValues = [LlmProvider.OpenRouter, LlmProvider.Anthropic];
    private static readonly string[] ProviderLabels = ["OpenRouter", "Anthropic (Claude API)"];

    // Values stored in Configuration.ReasoningEffort; "" means omit the reasoning object.
    private static readonly string[] EffortValues = ["low", "medium", "high", ""];
    private static readonly string[] EffortLabels = ["low", "medium", "high", "none (omit)"];

    // Values of Configuration.AnthropicOutgoingEffort.
    private static readonly string[] ClaudeEffortValues = [Efforts.Low, Efforts.Medium, Efforts.High];

    private readonly Configuration configuration;
    private readonly Func<bool> chatHookInstalled;
    private readonly TranslationsTab translationsTab;
    private readonly AxisFont font;

    private static readonly string[] ModifierLabels = ["Ctrl", "Shift", "Alt", "None"];
    private static readonly BypassModifier[] ModifierValues =
        [BypassModifier.Ctrl, BypassModifier.Shift, BypassModifier.Alt, BypassModifier.None];

    private string modelBuffer = string.Empty;
    private string outgoingModelBuffer = string.Empty;
    private string anthropicModelBuffer = string.Empty;
    private string anthropicOutgoingModelBuffer = string.Empty;
    private string customStyleBuffer = string.Empty;
    private string fallbackBuffer = string.Empty;
    private int maxLogLinesBuffer;
    private int maxCacheEntriesBuffer;
    private List<XivChatType> channelChoices = [];
    private string bypassPrefixBuffer = string.Empty;
    private string glossaryBuffer = string.Empty;
    private string glossaryInfo = string.Empty;

    // Plaintext lives only while the window is open; decrypted on open, wiped on close.
    private readonly KeyEditor primaryKey;
    private readonly KeyEditor anthropicKey;
    private readonly KeyEditor secondaryKey;

    /// <param name="configuration">Settings to edit; saved via <see cref="Configuration.Save"/>.</param>
    /// <param name="cache">Translation cache edited on the Translations tab (fixed and cached translations).</param>
    /// <param name="saveCache">Saves the cache after an edit (off the draw thread).</param>
    /// <param name="chatHookInstalled">Whether the chat-box hook is installed (shown on the Vanilla chat tab).</param>
    public ConfigWindow(Configuration configuration, ITranslationCache cache, Action saveCache, Func<bool> chatHookInstalled)
        : base("JP/EN Chat Settings###JpEnChatConfig", ImGuiWindowFlags.NoCollapse)
    {
        this.configuration = configuration;
        this.chatHookInstalled = chatHookInstalled;
        translationsTab = new TranslationsTab(cache, saveCache);
        font = new AxisFont(configuration);

        primaryKey = new KeyEditor(
            "OpenRouter API key",
            "##openRouterKey",
            () => configuration.OpenRouterKey,
            v => configuration.OpenRouterKey = v,
            () => configuration.OpenRouterKeyProtected.Length > 0,
            Commit);
        anthropicKey = new KeyEditor(
            "Anthropic API key",
            "##anthropicKey",
            () => configuration.AnthropicKey,
            v => configuration.AnthropicKey = v,
            () => configuration.AnthropicKeyProtected.Length > 0,
            Commit);
        secondaryKey = new KeyEditor(
            "Secondary key (reserved for an optional classifier; unused for now)",
            "##secondaryKey",
            () => configuration.SecondaryKey,
            v => configuration.SecondaryKey = v,
            () => configuration.SecondaryKeyProtected.Length > 0,
            Commit);

        Size = new Vector2(520, 440);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>Raised after every save, on the draw thread.</summary>
    public event Action? Changed;

    public void Dispose()
    {
        primaryKey.Wipe();
        anthropicKey.Wipe();
        secondaryKey.Wipe();
        font.Dispose();
    }

    public override void OnOpen()
    {
        modelBuffer = configuration.Model;
        outgoingModelBuffer = configuration.OutgoingModel;
        anthropicModelBuffer = configuration.AnthropicModel;
        anthropicOutgoingModelBuffer = configuration.AnthropicOutgoingModel;
        customStyleBuffer = configuration.CustomStyleText;
        fallbackBuffer = string.Join('\n', configuration.FallbackModels);
        maxLogLinesBuffer = configuration.MaxLogLines;
        maxCacheEntriesBuffer = configuration.MaxCacheEntries;
        channelChoices = Configuration.DefaultChannels()
            .Concat(configuration.EnabledChannels)
            .Distinct()
            .ToList();
        bypassPrefixBuffer = configuration.BypassPrefix;
        glossaryBuffer = configuration.UserGlossary;
        glossaryInfo = DescribeGlossary(glossaryBuffer);
        translationsTab.Reset();
        primaryKey.Load();
        anthropicKey.Load();
        secondaryKey.Load();
    }

    public override void OnClose()
    {
        primaryKey.Wipe();
        anthropicKey.Wipe();
        secondaryKey.Wipe();
        translationsTab.Reset();
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("##jpenConfigTabs");
        if (!tabs.Success)
        {
            return;
        }

        DrawTab("General", DrawGeneral);
        DrawTab("Translation", DrawTranslation);
        DrawTab("Vanilla chat", DrawVanillaChat);
        DrawTab("Channels", DrawChannels);
        DrawTab("Translations", DrawTranslations);
        DrawTab("Glossary", DrawGlossary);
        DrawTab("Keys", DrawKeys);
    }

    private static void DrawTab(string label, Action draw)
    {
        using var tab = ImRaii.TabItem(label);
        if (tab.Success)
        {
            ImGuiHelpers.ScaledDummy(2f);
            draw();
        }
    }

    private void Commit()
    {
        configuration.Save();
        Changed?.Invoke();
    }

    private void CommitIfReleased()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            Commit();
        }
    }

    // ---- General ----

    private void DrawGeneral()
    {
        var fontSize = (int)MathF.Round(configuration.FontSizePx);
        if (ImGui.SliderInt("Font size", ref fontSize, 10, 24, "%d px"))
        {
            configuration.FontSizePx = fontSize;
        }

        CommitIfReleased();

        var showTimestamps = configuration.ShowTimestamps;
        if (ImGui.Checkbox("Show timestamps", ref showTimestamps))
        {
            configuration.ShowTimestamps = showTimestamps;
            Commit();
        }

        ImGui.InputInt("Max log lines", ref maxLogLinesBuffer, 0, 0);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            maxLogLinesBuffer = Math.Clamp(maxLogLinesBuffer, ChatLog.MinMaxLines, 20000);
            configuration.MaxLogLines = maxLogLinesBuffer;
            Commit();
        }

        ImGuiComponents.HelpMarker("Oldest rows are dropped past this many. Applies when the next row arrives.");

        var debounce = configuration.DebounceMs;
        if (ImGui.SliderInt("Debounce", ref debounce, 0, 1000, "%d ms"))
        {
            configuration.DebounceMs = debounce;
        }

        CommitIfReleased();
        ImGuiComponents.HelpMarker(
            "How long to wait for more lines from the same sender before translating, so macro bursts go in one request.");

        ImGui.Separator();
        ImGui.TextUnformatted("Outgoing style (Japanese you send)");
        for (var i = 0; i < Styles.All.Length; i++)
        {
            var style = Styles.All[i];
            if (i > 0)
            {
                ImGui.SameLine();
            }

            if (ImGui.RadioButton(Styles.Label(style), configuration.DefaultStyle == style))
            {
                configuration.DefaultStyle = style;
                Commit();
            }
        }

        ImGuiComponents.HelpMarker(
            "The style the translate popup starts in; you can switch per message there.\n"
            + "Polite: friendly です/ます (strangers, Party Finder).\n"
            + "Casual: タメ口 among friends.\n"
            + "Cool: calm, composed and concise; polite-leaning but not stiff, short sentences (\"let's go\" → 行こうか。).\n"
            + "Custom: your own persona text below.");

        ImGui.TextUnformatted("Custom persona");
        ImGui.InputTextMultiline(
            "##customStyle",
            ref customStyleBuffer,
            CustomStyleMaxBytes,
            new Vector2(-1f, ImGui.GetTextLineHeightWithSpacing() * 3f));
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            configuration.CustomStyleText = customStyleBuffer.Trim();
            customStyleBuffer = configuration.CustomStyleText;
            Commit();
        }

        ImGui.TextDisabled(customStyleBuffer.Trim().Length == 0
            ? "e.g. \"speak like a cheerful Lalafell\". Empty: Custom falls back to Polite."
            : $"{LlmTranslator.CleanCustomStyle(customStyleBuffer).Length}/{LlmTranslator.MaxCustomStyleChars} characters sent. Saved when the box loses focus.");

        ImGui.Separator();
        ImGui.TextUnformatted("Party Finder");

        var pfMenu = configuration.PartyFinderContextMenu;
        if (ImGui.Checkbox("Add 'Translate' to the Party Finder right-click menu", ref pfMenu))
        {
            configuration.PartyFinderContextMenu = pfMenu;
            Commit();
        }

        ImGuiComponents.HelpMarker(
            "Right-click a listing's detail window in the Party Finder and choose Translate to translate its description. "
            + "The translation is also added to the log as a [PF] row.");

        var pfPopup = configuration.PartyFinderPopup;
        if (ImGui.Checkbox("Show Party Finder translations in a popup next to the listing", ref pfPopup))
        {
            configuration.PartyFinderPopup = pfPopup;
            Commit();
        }

        ImGuiComponents.HelpMarker("When off, the translation only goes to the log and the JP/EN chat window is opened.");
    }

    // ---- Translation ----

    private void DrawTranslation()
    {
        var providerIndex = Math.Max(Array.IndexOf(ProviderValues, configuration.Provider), 0);
        using (var combo = ImRaii.Combo("Provider", ProviderLabels[providerIndex]))
        {
            if (combo.Success)
            {
                for (var i = 0; i < ProviderValues.Length; i++)
                {
                    if (ImGui.Selectable(ProviderLabels[i], i == providerIndex))
                    {
                        configuration.Provider = ProviderValues[i];
                        Commit();
                    }
                }
            }
        }

        ImGuiComponents.HelpMarker(
            "OpenRouter: many models behind one key, billed by OpenRouter.\n"
            + "Anthropic: Claude models on the Claude API directly. Max/Team monthly API credits apply only here. "
            + "Set the matching key on the Keys tab. Applies to the next request.");

        if (!configuration.HasActiveKey)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "No key for this provider yet: see the Keys tab.");
        }

        ImGui.Separator();
        if (configuration.Provider == LlmProvider.Anthropic)
        {
            DrawAnthropicModels();
        }
        else
        {
            DrawOpenRouterModels();
        }

        ImGui.Separator();
        var contextLines = configuration.ContextLines;
        if (ImGui.SliderInt("Context lines", ref contextLines, 0, TranslationContext.MaxContextLines))
        {
            configuration.ContextLines = contextLines;
        }

        CommitIfReleased();
        ImGuiComponents.HelpMarker(
            "Each request also sends your zone, duty, job and channel, plus this many recent lines of the same channel "
            + "(or tell conversation) with their translations, so the model can resolve references. 0 sends no recent lines.");

        var concurrency = configuration.MaxConcurrency;
        if (ImGui.SliderInt("Max concurrent requests", ref concurrency, 1, 4))
        {
            configuration.MaxConcurrency = concurrency;
        }

        CommitIfReleased();

        var timeout = configuration.RequestTimeoutSeconds;
        if (ImGui.SliderInt("Request timeout", ref timeout, 3, 30, "%d s"))
        {
            configuration.RequestTimeoutSeconds = timeout;
        }

        CommitIfReleased();
    }

    private void DrawOpenRouterModels()
    {
        DrawModelField("Model (incoming JA→EN)", "##model", ref modelBuffer, OpenRouterPresets, v => configuration.Model = v);

        ImGui.TextUnformatted("Fallback models (one per line)");
        ImGui.InputTextMultiline(
            "##fallbackModels",
            ref fallbackBuffer,
            2048,
            new Vector2(-1f, ImGui.GetTextLineHeightWithSpacing() * 4f));
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            configuration.FallbackModels = fallbackBuffer
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            fallbackBuffer = string.Join('\n', configuration.FallbackModels);
            Commit();
        }

        var effortIndex = Array.IndexOf(EffortValues, configuration.ReasoningEffort);
        var preview = effortIndex >= 0 ? EffortLabels[effortIndex] : configuration.ReasoningEffort;
        using (var combo = ImRaii.Combo("Reasoning effort", preview))
        {
            if (combo.Success)
            {
                for (var i = 0; i < EffortValues.Length; i++)
                {
                    if (ImGui.Selectable(EffortLabels[i], i == effortIndex))
                    {
                        configuration.ReasoningEffort = EffortValues[i];
                        Commit();
                    }
                }
            }
        }

        ImGuiComponents.HelpMarker(
            "\"low\" is the fastest setting Gemini 3.x Flash accepts. \"none\" omits the reasoning parameter, for models without thinking.");

        ImGui.Separator();
        DrawModelField(
            "Outgoing model (EN→JA)", "##outgoingModel", ref outgoingModelBuffer, OpenRouterPresets, v => configuration.OutgoingModel = v);
    }

    private void DrawAnthropicModels()
    {
        DrawModelField(
            "Model (incoming JA→EN)", "##anthropicModel", ref anthropicModelBuffer, AnthropicPresets, v => configuration.AnthropicModel = v);
        ImGui.TextDisabled("Incoming batches always use effort \"low\" for speed.");

        ImGui.Separator();
        DrawModelField(
            "Outgoing model (EN→JA)",
            "##anthropicOutgoingModel",
            ref anthropicOutgoingModelBuffer,
            AnthropicPresets,
            v => configuration.AnthropicOutgoingModel = v);

        var effortIndex = Math.Max(Array.IndexOf(ClaudeEffortValues, configuration.AnthropicOutgoingEffort), 0);
        using (var combo = ImRaii.Combo("Outgoing effort", ClaudeEffortValues[effortIndex]))
        {
            if (combo.Success)
            {
                for (var i = 0; i < ClaudeEffortValues.Length; i++)
                {
                    if (ImGui.Selectable(ClaudeEffortValues[i], i == effortIndex))
                    {
                        configuration.AnthropicOutgoingEffort = ClaudeEffortValues[i];
                        Commit();
                    }
                }
            }
        }

        ImGuiComponents.HelpMarker("How much the model thinks before writing your Japanese. Higher is slower and costs more output tokens.");

        var fallback = configuration.AnthropicRefusalFallback;
        if (ImGui.Checkbox("Retry refusals on Anthropic's fallback model (Sonnet/Opus 5.5)", ref fallback))
        {
            configuration.AnthropicRefusalFallback = fallback;
            Commit();
        }

        ImGuiComponents.HelpMarker(
            "Sends fallbacks: \"default\" so a request a safety classifier declines is re-run server-side on the model Anthropic "
            + "recommends. Never used with Haiku.");
    }

    /// <summary>Model id text box with a preset combo next to it. The text applies on focus loss.</summary>
    private void DrawModelField(string label, string id, ref string buffer, ModelPreset[] presets, Action<string> apply)
    {
        ImGui.TextUnformatted(label);
        var comboWidth = ImGui.GetFontSize() * 7f;
        ImGui.SetNextItemWidth(Math.Max(ImGui.GetContentRegionAvail().X - comboWidth - ImGui.GetStyle().ItemSpacing.X, 100f));
        ImGui.InputText(id, ref buffer, ModelMaxBytes);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            buffer = buffer.Trim();
            if (buffer.Length == 0)
            {
                buffer = presets[0].Id;
            }

            apply(buffer);
            Commit();
        }

        var current = buffer;
        if (ImGui.IsItemHovered() && Array.Find(presets, p => string.Equals(p.Id, current, StringComparison.Ordinal)) is { Price: { } price })
        {
            ImGui.SetTooltip(price);
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(comboWidth);
        using var combo = ImRaii.Combo($"{id}Preset", "Presets", ImGuiComboFlags.HeightLarge);
        if (!combo.Success)
        {
            return;
        }

        foreach (var preset in presets)
        {
            if (ImGui.Selectable(preset.Label, string.Equals(preset.Id, buffer, StringComparison.Ordinal)))
            {
                buffer = preset.Id;
                apply(preset.Id);
                Commit();
            }
        }
    }

    // ---- Vanilla chat (PLAN §9) ----

    private void DrawVanillaChat()
    {
        ImGui.TextWrapped(
            "When on, plain English you type into the game's own chat box is held back when you press Enter and "
            + "translated in a small popup next to the chat box. Press Enter in the popup to send the Japanese, "
            + "Shift+Enter to send your English as typed, Esc to cancel (your text is put back into the chat box).");
        ImGui.Spacing();

        if (!chatHookInstalled())
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "Unavailable this session: the chat-box hook could not be installed (see /xllog).");
        }

        var intercept = configuration.InterceptVanillaChat;
        if (ImGui.Checkbox("Translate English typed into the game's chat box", ref intercept))
        {
            configuration.InterceptVanillaChat = intercept;
            Commit();
        }

        ImGuiComponents.HelpMarker(
            "Only lines that are plain English are held back. Japanese, commands such as /dance or /xlplugins, /e echo, "
            + "links, numbers and item links are always sent unchanged. Toggle quickly with /jpchat auto.");

        var modifierIndex = Math.Max(Array.IndexOf(ModifierValues, configuration.BypassModifier), 0);
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6f);
        using (var combo = ImRaii.Combo("Send untranslated with modifier + Enter", ModifierLabels[modifierIndex]))
        {
            if (combo.Success)
            {
                for (var i = 0; i < ModifierValues.Length; i++)
                {
                    if (ImGui.Selectable(ModifierLabels[i], i == modifierIndex))
                    {
                        configuration.BypassModifier = ModifierValues[i];
                        Commit();
                    }
                }
            }
        }

        ImGuiComponents.HelpMarker("Hold this key while pressing Enter in the game's chat box to send English to English-speaking friends as typed. Default Ctrl (Ctrl+Enter).");

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 4f);
        ImGui.InputText("Bypass prefix", ref bypassPrefixBuffer, Configuration.MaxBypassPrefixLength * 4);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            configuration.BypassPrefix = Configuration.NormalizeBypassPrefix(bypassPrefixBuffer);
            bypassPrefixBuffer = configuration.BypassPrefix;
            Commit();
        }

        ImGuiComponents.HelpMarker(
            $"A line starting with this is sent untranslated, without the prefix: \"{configuration.BypassPrefix}hello\" sends \"hello\". "
            + "Also works after a channel command (\"/p " + configuration.BypassPrefix + "hello\"). 1–3 characters, not starting with /.");

        ImGui.Separator();
        ImGui.TextUnformatted("Popup position");
        ImGui.TextDisabled("Placed right of the chat box (above it if there is no room), then moved by these offsets.");
        DrawOffsetPair("##popupOffset", () => configuration.PopupOffsetX, v => configuration.PopupOffsetX = v,
            () => configuration.PopupOffsetY, v => configuration.PopupOffsetY = v, 0, 0);

        ImGui.Separator();
        var showButton = configuration.ShowChatBarButton;
        if (ImGui.Checkbox("Show the log button on the chat tab bar", ref showButton))
        {
            configuration.ShowChatBarButton = showButton;
            Commit();
        }

        ImGuiComponents.HelpMarker("A small button next to the game's chat tabs that opens or closes the JP/EN chat window.");
        ImGui.TextDisabled("Placed right of the last chat tab, then moved by these offsets.");
        DrawOffsetPair("##buttonOffset", () => configuration.ChatBarButtonOffsetX, v => configuration.ChatBarButtonOffsetX = v,
            () => configuration.ChatBarButtonOffsetY, v => configuration.ChatBarButtonOffsetY = v,
            Configuration.DefaultChatBarButtonOffsetX, Configuration.DefaultChatBarButtonOffsetY);
    }

    /// <summary>X and Y sliders (-400..400 px) plus a Reset button. Sliders apply live and save on release.</summary>
    private void DrawOffsetPair(
        string id, Func<int> getX, Action<int> setX, Func<int> getY, Action<int> setY, int defaultX, int defaultY)
    {
        using var scope = ImRaii.PushId(id);
        var x = getX();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12f);
        if (ImGui.SliderInt("Offset X", ref x, -400, 400, "%d px"))
        {
            setX(x);
        }

        CommitIfReleased();

        var y = getY();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12f);
        if (ImGui.SliderInt("Offset Y", ref y, -400, 400, "%d px"))
        {
            setY(y);
        }

        CommitIfReleased();

        ImGui.SameLine();
        if (ImGui.Button("Reset"))
        {
            setX(defaultX);
            setY(defaultY);
            Commit();
        }
    }

    // ---- Channels ----

    private void DrawChannels()
    {
        ImGui.TextWrapped("Chat channels captured into the window and considered for translation.");

        if (ImGui.Button("All"))
        {
            configuration.EnabledChannels = [.. channelChoices];
            Commit();
        }

        ImGui.SameLine();
        if (ImGui.Button("None"))
        {
            configuration.EnabledChannels = [];
            Commit();
        }

        ImGui.SameLine();
        if (ImGui.Button("Defaults"))
        {
            configuration.EnabledChannels = Configuration.DefaultChannels();
            Commit();
        }

        using var table = ImRaii.Table("##jpenChannelGrid", 3, ImGuiTableFlags.SizingStretchSame);
        if (!table.Success)
        {
            return;
        }

        foreach (var kind in channelChoices)
        {
            ImGui.TableNextColumn();
            var enabled = configuration.EnabledChannels.Contains(kind);
            if (ImGui.Checkbox(ChatChannels.DisplayName(kind), ref enabled))
            {
                if (enabled)
                {
                    configuration.EnabledChannels.Add(kind);
                }
                else
                {
                    configuration.EnabledChannels.Remove(kind);
                }

                Commit();
            }
        }
    }

    // ---- Translations (PLAN §3.2, §11) ----

    private void DrawTranslations()
    {
        var enabled = configuration.CacheEnabled;
        if (ImGui.Checkbox("Cache translations", ref enabled))
        {
            configuration.CacheEnabled = enabled;
            Commit();
        }

        ImGuiComponents.HelpMarker("Fixed translations apply even when the cache is off.");

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6f);
        ImGui.InputInt("Max cached entries", ref maxCacheEntriesBuffer, 0, 0);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            maxCacheEntriesBuffer = Math.Clamp(maxCacheEntriesBuffer, 100, 100000);
            configuration.MaxCacheEntries = maxCacheEntriesBuffer;
            Commit();
        }

        ImGuiComponents.HelpMarker("The oldest cached translations are dropped past this many. Fixed translations do not count.");

        font.Ensure();
        using (font.Push())
        {
            translationsTab.Draw();
        }
    }

    // ---- Glossary (PLAN §11) ----

    private void DrawGlossary()
    {
        ImGui.TextWrapped(
            "One term per line, e.g. \"ノ = o/ (raised hand)\". These notes are sent to the model after the built-in glossary, "
            + "with every incoming and outgoing request, and take priority over it.");
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
        {
            ImGui.TextWrapped(
                "For one exact message, a fixed translation (Translations tab, or right-click a row in the log) is cheaper and always applies.");
        }
        ImGui.Spacing();

        font.Ensure();
        using (font.Push())
        {
            if (glossaryBuffer.Length == 0)
            {
                ImGui.TextDisabled("Example:  ノ = o/ (raised hand)    竜騎士 = Dragoon (DRG)");
            }

            var height = Math.Max(ImGui.GetContentRegionAvail().Y - ImGui.GetTextLineHeightWithSpacing() * 2f, ImGui.GetTextLineHeightWithSpacing() * 6f);
            if (ImGui.InputTextMultiline("##userGlossary", ref glossaryBuffer, Prompts.MaxUserGlossaryChars * 3, new Vector2(-1f, height)))
            {
                glossaryInfo = DescribeGlossary(glossaryBuffer);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                configuration.UserGlossary = glossaryBuffer;
                Commit();
            }
        }

        ImGui.TextDisabled(glossaryInfo);
    }

    private static string DescribeGlossary(string text)
    {
        var sent = Prompts.CleanGlossary(text);
        if (sent.Length == 0)
        {
            return "Empty: nothing is added to the prompt. Saved when the box loses focus.";
        }

        var lines = sent.Count(c => c == '\n') + 1;
        var all = string.Join('\n', text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        var cut = all.Length > sent.Length;
        return $"{lines} line(s), {sent.Length}/{Prompts.MaxUserGlossaryChars} characters sent"
               + (cut ? " (cut: lines past the limit are not sent)" : string.Empty) + ". Saved when the box loses focus.";
    }

    // ---- Keys ----

    private void DrawKeys()
    {
        primaryKey.Draw();
        ImGui.Separator();
        anthropicKey.Draw();
        ImGui.TextDisabled("Max/Team monthly API credits apply only to this provider.");
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
        {
            ImGui.TextWrapped(
                "Get a key at console.anthropic.com → API keys. To spend your plan's monthly API credits, link the Console "
                + "organization to your plan in claude.ai → Settings → Billing. Then choose Provider: Anthropic on the Translation tab.");
        }

        ImGui.Separator();
        secondaryKey.Draw();
        ImGui.Spacing();
        ImGui.TextDisabled("Keys are encrypted with Windows DPAPI for this Windows user and never logged.");
    }

    /// <summary>Password field + Save button for one DPAPI-protected key.</summary>
    private sealed class KeyEditor(
        string label,
        string id,
        Func<string> read,
        Action<string> write,
        Func<bool> isStored,
        Action commit)
    {
        private string buffer = string.Empty;
        private string status = string.Empty;

        public void Load()
        {
            buffer = read();
            status = string.Empty;
        }

        public void Wipe()
        {
            buffer = string.Empty;
            status = string.Empty;
        }

        public void Draw()
        {
            using var scope = ImRaii.PushId(id);
            ImGui.TextUnformatted(label);
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputText(id, ref buffer, KeyMaxBytes, ImGuiInputTextFlags.Password);

            if (ImGui.Button("Save"))
            {
                try
                {
                    write(buffer.Trim());
                    commit();
                    status = isStored() ? "Saved (encrypted for this Windows user)." : "Key cleared.";
                }
                catch (Exception ex)
                {
                    // Log the exception type only; never the key.
                    Services.Log.Error($"Failed to save API key: {ex.GetType().Name}");
                    status = "Could not save the key; see /xllog.";
                }
            }

            ImGui.SameLine();
            if (isStored())
            {
                ImGui.TextColored(ImGuiColors.HealerGreen, "Key stored");
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, "No key set");
            }

            if (status.Length > 0)
            {
                ImGui.TextDisabled(status);
            }
        }
    }

    /// <summary>A model id offered in a preset combo, with an optional price hint.</summary>
    private sealed record ModelPreset(string Id, string? Price = null)
    {
        public string Label => Price is null ? Id : $"{Id}  ({Price})";
    }
}
