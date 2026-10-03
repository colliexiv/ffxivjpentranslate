using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;

namespace JpEnChat.Windows;

/// <summary>
/// Settings window. Phase 1: OpenRouter API key entry only.
/// TODO(Phase 2B): model / fallback / reasoning effort, channels, debounce, concurrency, timeout, font size,
/// cache controls (size, clear), default register, secondary key.
/// </summary>
public sealed class ConfigWindow : Window, IDisposable
{
    private const int KeyMaxBytes = 256;

    private readonly Configuration configuration;

    // Plaintext lives only while the window is open; decrypted on open, wiped on close.
    private string keyBuffer = string.Empty;
    private bool hasStoredKey;
    private string status = string.Empty;

    public ConfigWindow(Configuration configuration)
        : base("JP/EN Chat Settings###JpEnChatConfig", ImGuiWindowFlags.NoCollapse)
    {
        this.configuration = configuration;

        Size = new Vector2(420, 150);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
        keyBuffer = string.Empty;
    }

    public override void OnOpen()
    {
        keyBuffer = configuration.OpenRouterKey;
        hasStoredKey = keyBuffer.Length > 0;
        status = string.Empty;
    }

    public override void OnClose()
    {
        keyBuffer = string.Empty;
        status = string.Empty;
    }

    public override void Draw()
    {
        ImGui.Text("OpenRouter API key");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##openRouterKey", ref keyBuffer, KeyMaxBytes, ImGuiInputTextFlags.Password);

        if (ImGui.Button("Save"))
        {
            try
            {
                configuration.OpenRouterKey = keyBuffer.Trim();
                configuration.Save();
                hasStoredKey = configuration.OpenRouterKeyProtected.Length > 0;
                status = hasStoredKey ? "Saved (encrypted for this Windows user)." : "Key cleared.";
            }
            catch (Exception ex)
            {
                // Log the exception type only; never the key.
                Services.Log.Error($"Failed to save API key: {ex.GetType().Name}");
                status = "Could not save the key; see /xllog.";
            }
        }

        ImGui.SameLine();
        if (hasStoredKey)
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
