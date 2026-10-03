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
using JpEnChat.Ui;

namespace JpEnChat.Windows;

/// <summary>
/// Settings window: General, Translation, Channels, Cache and Keys tabs (PLAN §5).
/// </summary>
/// <remarks>
/// Toggles and combos save immediately. Sliders apply live (so the font size previews while dragging) and save when
/// released. Free-text and numeric fields apply and save when the field loses focus, so a half-typed value (e.g. a
/// log limit of "5") never takes effect. Every save raises <see cref="Changed"/>.
/// API keys are decrypted into buffers only while the window is open and wiped on close.
/// </remarks>
public sealed class ConfigWindow : Window, IDisposable
{
    private const int KeyMaxBytes = 256;
    private const int ModelMaxBytes = 128;

    private static readonly string[] PresetModels =
    [
        "google/gemini-3.8-flash",
        "google/gemini-3.5-flash-lite",
        "google/gemini-2.5-flash-lite",
        "openai/gpt-4.1-mini",
        "anthropic/claude-haiku-4.5",
    ];

    // Values stored in Configuration.ReasoningEffort; "" means omit the reasoning object.
    private static readonly string[] EffortValues = ["low", "medium", "high", ""];
    private static readonly string[] EffortLabels = ["low", "medium", "high", "none (omit)"];

    private readonly Configuration configuration;
    private readonly Action clearCache;
    private readonly Func<int> cacheEntryCount;

    private string modelBuffer = string.Empty;
    private string outgoingModelBuffer = string.Empty;
    private string fallbackBuffer = string.Empty;
    private int maxLogLinesBuffer;
    private int maxCacheEntriesBuffer;
    private List<XivChatType> channelChoices = [];
    private string cacheStatus = string.Empty;

    // Plaintext lives only while the window is open; decrypted on open, wiped on close.
    private readonly KeyEditor primaryKey;
    private readonly KeyEditor secondaryKey;

    /// <param name="configuration">Settings to edit; saved via <see cref="Configuration.Save"/>.</param>
    /// <param name="clearCache">Clears the translation cache (Cache tab button).</param>
    /// <param name="cacheEntryCount">Current number of cache entries, shown on the Cache tab.</param>
    public ConfigWindow(Configuration configuration, Action clearCache, Func<int> cacheEntryCount)
        : base("JP/EN Chat Settings###JpEnChatConfig", ImGuiWindowFlags.NoCollapse)
    {
        this.configuration = configuration;
        this.clearCache = clearCache;
        this.cacheEntryCount = cacheEntryCount;

        primaryKey = new KeyEditor(
            "OpenRouter API key",
            "##openRouterKey",
            () => configuration.OpenRouterKey,
            v => configuration.OpenRouterKey = v,
            () => configuration.OpenRouterKeyProtected.Length > 0,
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
        secondaryKey.Wipe();
    }

    public override void OnOpen()
    {
        modelBuffer = configuration.Model;
        outgoingModelBuffer = configuration.OutgoingModel;
        fallbackBuffer = string.Join('\n', configuration.FallbackModels);
        maxLogLinesBuffer = configuration.MaxLogLines;
        maxCacheEntriesBuffer = configuration.MaxCacheEntries;
        channelChoices = Configuration.DefaultChannels()
            .Concat(configuration.EnabledChannels)
            .Distinct()
            .ToList();
        cacheStatus = string.Empty;
        primaryKey.Load();
        secondaryKey.Load();
    }

    public override void OnClose()
    {
        primaryKey.Wipe();
        secondaryKey.Wipe();
        cacheStatus = string.Empty;
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
        DrawTab("Channels", DrawChannels);
        DrawTab("Cache", DrawCache);
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

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Default register:");
        ImGui.SameLine();
        if (ImGui.RadioButton("polite", configuration.DefaultRegister == Registers.Polite))
        {
            configuration.DefaultRegister = Registers.Polite;
            Commit();
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("casual", configuration.DefaultRegister == Registers.Casual))
        {
            configuration.DefaultRegister = Registers.Casual;
            Commit();
        }
    }

    // ---- Translation ----

    private void DrawTranslation()
    {
        DrawModelField("Model (incoming JA→EN)", "##model", ref modelBuffer, v => configuration.Model = v);

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
            "Outgoing model (EN→JA)", "##outgoingModel", ref outgoingModelBuffer, v => configuration.OutgoingModel = v);

        ImGui.Separator();
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

    /// <summary>Model id text box with a preset combo next to it. The text applies on focus loss.</summary>
    private void DrawModelField(string label, string id, ref string buffer, Action<string> apply)
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
                buffer = PresetModels[0];
            }

            apply(buffer);
            Commit();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(comboWidth);
        using var combo = ImRaii.Combo($"{id}Preset", "Presets", ImGuiComboFlags.HeightLarge);
        if (!combo.Success)
        {
            return;
        }

        foreach (var preset in PresetModels)
        {
            if (ImGui.Selectable(preset, string.Equals(preset, buffer, StringComparison.Ordinal)))
            {
                buffer = preset;
                apply(preset);
                Commit();
            }
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

    // ---- Cache ----

    private void DrawCache()
    {
        var enabled = configuration.CacheEnabled;
        if (ImGui.Checkbox("Cache translations", ref enabled))
        {
            configuration.CacheEnabled = enabled;
            Commit();
        }

        ImGui.InputInt("Max entries", ref maxCacheEntriesBuffer, 0, 0);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            maxCacheEntriesBuffer = Math.Clamp(maxCacheEntriesBuffer, 100, 100000);
            configuration.MaxCacheEntries = maxCacheEntriesBuffer;
            Commit();
        }

        ImGui.TextUnformatted($"Entries: {cacheEntryCount()}");

        if (ImGui.Button("Clear cache"))
        {
            clearCache();
            cacheStatus = "Cache cleared.";
        }

        if (cacheStatus.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(cacheStatus);
        }
    }

    // ---- Keys ----

    private void DrawKeys()
    {
        primaryKey.Draw();
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
}
