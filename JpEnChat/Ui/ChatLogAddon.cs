using System;
using System.Numerics;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace JpEnChat.Ui;

/// <summary>
/// Reads the vanilla <c>ChatLog</c> addon's on-screen geometry for the overlays (popup, chat-bar button), and writes
/// text into its input box. Framework/draw thread only; every read is null-checked because the addon does not exist
/// during loading screens and some cutscenes.
/// </summary>
/// <remarks>
/// Addon coordinates are pixels relative to the game window's client area; ImGui coordinates are relative to the
/// screen, so <see cref="ImGuiHelpers.MainViewport"/>'s position is added.
/// </remarks>
internal static unsafe class ChatLogAddon
{
    private const string AddonName = "ChatLog";

    /// <summary>Screen rectangle of the chat window (scaled), or false when it is missing or hidden.</summary>
    public static bool TryGetRect(out Vector2 position, out Vector2 size)
    {
        var addon = Services.GameGui.GetAddonByName<AddonChatLog>(AddonName);
        return AddonRect.TryGet(addon == null ? null : &addon->AtkUnitBase, out position, out size);
    }

    /// <summary>
    /// Top-right corner of the last chat tab (the right end of the tab bar) and the tab's scaled height. Falls back
    /// to the chat window's top-left corner with a default height when no tab node is available.
    /// </summary>
    public static bool TryGetTabBarEnd(out Vector2 anchor, out float height)
    {
        anchor = default;
        height = 0f;
        var addon = Services.GameGui.GetAddonByName<AddonChatLog>(AddonName);
        if (addon == null || !addon->AtkUnitBase.IsVisible)
        {
            return false;
        }

        var unit = &addon->AtkUnitBase;
        var scale = unit->Scale > 0f ? unit->Scale : 1f;
        var viewport = ImGuiHelpers.MainViewport.Pos;
        var tabs = addon->ChatTabs;
        var count = Math.Min((int)addon->TabCount, tabs.Length);
        for (var i = count - 1; i >= 0; i--)
        {
            var tab = tabs[i].Value;
            if (tab == null || tab->OwnerNode == null)
            {
                continue;
            }

            var node = tab->OwnerNode;
            if (node->Width == 0 || node->AtkResNode.Height == 0)
            {
                continue;
            }

            anchor = viewport + new Vector2(node->ScreenX + (node->Width * scale), node->ScreenY);
            height = node->AtkResNode.Height * scale;
            return true;
        }

        anchor = viewport + new Vector2(unit->X, unit->Y);
        height = 24f * scale;
        return true;
    }

    /// <summary>Puts <paramref name="text"/> into the chat box's input. False when the addon or input is missing.</summary>
    public static bool TrySetInputText(string text)
    {
        var addon = Services.GameGui.GetAddonByName<AddonChatLog>(AddonName);
        if (addon == null || addon->TextInput == null)
        {
            return false;
        }

        addon->TextInput->SetText(text);
        return true;
    }
}
