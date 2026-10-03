using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Game.Text;
using JpEnChat.Models;
using JpEnChat.Security;
using Newtonsoft.Json;

namespace JpEnChat;

/// <summary>Modifier key that, held while pressing Enter in the game's chat box, sends the line untranslated.</summary>
public enum BypassModifier
{
    Ctrl,
    Shift,
    Alt,
    None,
}

/// <summary>
/// Persisted plugin settings. Saved by Dalamud as JSON under <c>pluginConfigs/JpEnChat.json</c>.
/// </summary>
/// <remarks>
/// Dalamud (API 15) serializes plugin configs with Newtonsoft.Json, so the Newtonsoft attributes are the ones
/// that take effect. Collections use <see cref="ObjectCreationHandling.Replace"/> so loading a saved list
/// replaces the defaults instead of appending to them (Newtonsoft's default would resurrect channels the user
/// disabled and duplicate entries on every load).
/// Read/write from the framework or draw thread only; this type is not synchronized.
/// </remarks>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    /// <summary>Schema version written by this build. Bump and migrate in <see cref="Migrate"/> on breaking changes.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    // ---- Ingest (PLAN §3.1) ----

    /// <summary>Chat channels that are captured into the window and considered for translation.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<XivChatType> EnabledChannels { get; set; } = DefaultChannels();

    /// <summary>Per-sender debounce window used to merge macro bursts into one request (PLAN §3.2).</summary>
    public int DebounceMs { get; set; } = 300;

    // ---- Translator (PLAN §3.3, §8) ----

    /// <summary>OpenRouter model id for incoming JA→EN translation.</summary>
    public string Model { get; set; } = "google/gemini-3.8-flash";

    /// <summary>Sent as OpenRouter's <c>models</c> fallback list after <see cref="Model"/>.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> FallbackModels { get; set; } = ["google/gemini-3.5-flash-lite"];

    /// <summary>
    /// OpenRouter <c>reasoning.effort</c>: "low", "medium" or "high". "low" is the floor for Gemini 3.7/3.8 Flash
    /// ("minimal" is a 400). An empty string means "omit the <c>reasoning</c> object" (for models without thinking).
    /// </summary>
    public string ReasoningEffort { get; set; } = "low";

    /// <summary>Model used for the outgoing EN→JA structured request, where quality matters more than latency.</summary>
    public string OutgoingModel { get; set; } = "google/gemini-3.8-flash";

    /// <summary>Maximum concurrent in-flight translation requests.</summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>Per-request timeout. No fallback chain to other services on expiry.</summary>
    public int RequestTimeoutSeconds { get; set; } = 8;

    /// <summary>Default politeness for outgoing translations; one of <see cref="Registers"/>.</summary>
    public string DefaultRegister { get; set; } = Registers.Polite;

    // ---- Cache (PLAN §3.2) ----

    public bool CacheEnabled { get; set; } = true;

    public int MaxCacheEntries { get; set; } = 2000;

    // ---- UI (PLAN §4) ----

    /// <summary>Font size for both panes, in pixels.</summary>
    public float FontSizePx { get; set; } = 14f;

    public bool ShowTimestamps { get; set; } = true;

    /// <summary>Maximum rows kept in the window's log; the oldest rows are dropped first.</summary>
    public int MaxLogLines { get; set; } = 2000;

    /// <summary>Channels hidden by the main window's channel filter (display only; capture is unaffected).</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<XivChatType> HiddenLogChannels { get; set; } = [];

    /// <summary>Reserved for v2 (hide the vanilla ChatLog addon). Unused in v1.</summary>
    public bool HideVanillaChat { get; set; }

    // ---- Vanilla chat integration (PLAN §9) ----

    /// <summary>Plain English typed into the game's chat box is translated in a popup before it is sent.</summary>
    public bool InterceptVanillaChat { get; set; } = true;

    /// <summary>Lines starting with this are sent untranslated, with the prefix removed. 1–3 characters.</summary>
    public string BypassPrefix { get; set; } = DefaultBypassPrefix;

    /// <summary>Holding this key while pressing Enter in the game's chat box sends the line untranslated.</summary>
    public BypassModifier BypassModifier { get; set; } = BypassModifier.Ctrl;

    /// <summary>Added to the quick-translate popup's position next to the chat box, in pixels (before UI scale).</summary>
    public int PopupOffsetX { get; set; }

    /// <inheritdoc cref="PopupOffsetX"/>
    public int PopupOffsetY { get; set; }

    /// <summary>Show the small button on the game's chat tab bar that toggles the main window.</summary>
    public bool ShowChatBarButton { get; set; } = true;

    /// <summary>Offset of the chat-bar button from its anchor (right end of the last chat tab), in pixels (before UI scale).</summary>
    public int ChatBarButtonOffsetX { get; set; } = DefaultChatBarButtonOffsetX;

    /// <inheritdoc cref="ChatBarButtonOffsetX"/>
    public int ChatBarButtonOffsetY { get; set; } = DefaultChatBarButtonOffsetY;

    // ---- Party Finder (PLAN §10) ----

    /// <summary>Add a "Translate" item to the right-click menu of a Party Finder listing's detail window.</summary>
    public bool PartyFinderContextMenu { get; set; } = true;

    /// <summary>
    /// Show a translated listing in a popup next to the listing. When off, only the log row is added and the main
    /// window is opened.
    /// </summary>
    public bool PartyFinderPopup { get; set; } = true;

    /// <summary>Party Finder rows are hidden by the main window's channel filter (display only).</summary>
    public bool PartyFinderHidden { get; set; }

    public const string DefaultBypassPrefix = "\\";
    public const int MaxBypassPrefixLength = 3;
    public const int DefaultChatBarButtonOffsetX = 4;
    public const int DefaultChatBarButtonOffsetY = 0;

    // ---- Secrets (PLAN §5) ----

    /// <summary>DPAPI-protected, base64 OpenRouter API key. Use <see cref="OpenRouterKey"/> instead.</summary>
    public string OpenRouterKeyProtected { get; set; } = string.Empty;

    /// <summary>DPAPI-protected, base64 secondary key (reserved for an optional classifier). Use <see cref="SecondaryKey"/>.</summary>
    public string SecondaryKeyProtected { get; set; } = string.Empty;

    /// <summary>
    /// Plaintext OpenRouter key, decrypted on every get. Empty when unset or undecryptable
    /// (e.g. config copied from another Windows account). Setting an empty string clears it. Never serialized.
    /// </summary>
    [JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public string OpenRouterKey
    {
        get => ProtectedSecret.Unprotect(OpenRouterKeyProtected) ?? string.Empty;
        set => OpenRouterKeyProtected = ProtectOrEmpty(value);
    }

    /// <summary>Plaintext secondary key; same semantics as <see cref="OpenRouterKey"/>. Never serialized.</summary>
    [JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public string SecondaryKey
    {
        get => ProtectedSecret.Unprotect(SecondaryKeyProtected) ?? string.Empty;
        set => SecondaryKeyProtected = ProtectOrEmpty(value);
    }

    /// <summary>Channels covered by default (PLAN §3.1).</summary>
    public static List<XivChatType> DefaultChannels() =>
    [
        XivChatType.Say,
        XivChatType.Shout,
        XivChatType.Yell,
        XivChatType.Party,
        XivChatType.Alliance,
        XivChatType.FreeCompany,
        XivChatType.Ls1,
        XivChatType.Ls2,
        XivChatType.Ls3,
        XivChatType.Ls4,
        XivChatType.Ls5,
        XivChatType.Ls6,
        XivChatType.Ls7,
        XivChatType.Ls8,
        XivChatType.CrossLinkShell1,
        XivChatType.CrossLinkShell2,
        XivChatType.CrossLinkShell3,
        XivChatType.CrossLinkShell4,
        XivChatType.CrossLinkShell5,
        XivChatType.CrossLinkShell6,
        XivChatType.CrossLinkShell7,
        XivChatType.CrossLinkShell8,
        XivChatType.TellIncoming,
        XivChatType.TellOutgoing,
        XivChatType.NoviceNetwork,
        XivChatType.PvPTeam,
        XivChatType.CrossParty,
        XivChatType.CustomEmote,
    ];

    /// <summary>Upgrades an older saved schema in place. Call once after loading.</summary>
    public void Migrate()
    {
        // No older schemas exist yet. Future: if (Version < 2) { ...; Version = 2; }
        // Fields added later in schema 1 load as their defaults; only values a hand-edit could break are repaired.
        BypassPrefix = NormalizeBypassPrefix(BypassPrefix);
        if (!Enum.IsDefined(BypassModifier))
        {
            BypassModifier = BypassModifier.Ctrl;
        }

        Version = CurrentVersion;
    }

    /// <summary>Persists this configuration via Dalamud.</summary>
    public void Save() => Services.PluginInterface.SavePluginConfig(this);

    /// <summary>
    /// A usable bypass prefix: trimmed, 1–<see cref="MaxBypassPrefixLength"/> characters, must not start with
    /// <c>/</c> (that would swallow chat commands). Anything else falls back to <see cref="DefaultBypassPrefix"/>.
    /// </summary>
    public static string NormalizeBypassPrefix(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length is >= 1 and <= MaxBypassPrefixLength && trimmed[0] != '/' ? trimmed : DefaultBypassPrefix;
    }

    private static string ProtectOrEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : ProtectedSecret.Protect(value);
}
