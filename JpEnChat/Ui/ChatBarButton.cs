using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace JpEnChat.Ui;

/// <summary>
/// A small icon button drawn over the game's chat tab bar, right of the last tab, that toggles the main window.
/// </summary>
/// <remarks>
/// A borderless, background-less ImGui window sized to the button, so it never blocks clicks outside its own rect.
/// Position: the right end of the last chat tab (<see cref="ChatLogAddon.TryGetTabBarEnd"/>) plus
/// <see cref="Configuration.ChatBarButtonOffsetX"/>/<see cref="Configuration.ChatBarButtonOffsetY"/> (scaled by the
/// Dalamud UI scale). Hidden when the chat window is missing or hidden, when the game UI is hidden, or when
/// <see cref="Configuration.ShowChatBarButton"/> is off.
/// </remarks>
internal sealed class ChatBarButton(Configuration configuration, Action toggleMainWindow)
{
    private const string WindowId = "##JpEnChatBarButton";

    private const ImGuiWindowFlags Flags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar
        | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
        | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoDocking
        | ImGuiWindowFlags.NoCollapse;

    private bool loggedError;

    /// <summary>Draws the button if it should be visible. Call from the UI draw handler.</summary>
    public void Draw()
    {
        if (!configuration.ShowChatBarButton || Services.GameGui.GameUiHidden)
        {
            return;
        }

        Vector2 anchor;
        float height;
        try
        {
            if (!ChatLogAddon.TryGetTabBarEnd(out anchor, out height))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            if (!loggedError)
            {
                loggedError = true;
                Services.Log.Warning($"[JpEnChat] chat-bar button: could not read the chat window ({ex.GetType().Name}).");
            }

            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var side = Math.Clamp(height, 16f * scale, 40f * scale);
        var position = anchor + (new Vector2(configuration.ChatBarButtonOffsetX, configuration.ChatBarButtonOffsetY) * scale);
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);

        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var border = ImRaii.PushStyle(ImGuiStyleVar.WindowBorderSize, 0f);
        using var minSize = ImRaii.PushStyle(ImGuiStyleVar.WindowMinSize, Vector2.One);

        if (ImGui.Begin(WindowId, Flags))
        {
            if (ImGuiComponents.IconButton(0x4A50, FontAwesomeIcon.Language, new Vector2(side, side)))
            {
                toggleMainWindow();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("JP/EN Chat log");
            }
        }

        ImGui.End();
    }
}
