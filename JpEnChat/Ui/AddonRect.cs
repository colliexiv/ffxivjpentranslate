using System.Numerics;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace JpEnChat.Ui;

/// <summary>On-screen geometry of a game addon (window), for overlays anchored to it. Framework/draw thread only.</summary>
/// <remarks>
/// Addon coordinates are pixels relative to the game window's client area; ImGui coordinates are relative to the
/// screen, so <see cref="ImGuiHelpers.MainViewport"/>'s position is added. Every read is null-checked: addons do not
/// exist during loading screens and while closed.
/// </remarks>
internal static unsafe class AddonRect
{
    /// <summary>Screen rectangle (scaled) of the addon named <paramref name="addonName"/>; false when it is missing or hidden.</summary>
    public static bool TryGet(string addonName, out Vector2 position, out Vector2 size) =>
        TryGet(Services.GameGui.GetAddonByName<AtkUnitBase>(addonName), out position, out size);

    /// <summary>Screen rectangle (scaled) of <paramref name="unit"/>; false when it is null, hidden or has no size.</summary>
    public static bool TryGet(AtkUnitBase* unit, out Vector2 position, out Vector2 size)
    {
        position = default;
        size = default;
        if (unit == null || !unit->IsVisible)
        {
            return false;
        }

        position = ImGuiHelpers.MainViewport.Pos + new Vector2(unit->X, unit->Y);
        size = new Vector2(unit->GetScaledWidth(true), unit->GetScaledHeight(true));
        return size.X > 0f && size.Y > 0f;
    }

    /// <summary>The addon named <paramref name="addonName"/> exists and is visible.</summary>
    public static bool IsVisible(string addonName)
    {
        var unit = Services.GameGui.GetAddonByName<AtkUnitBase>(addonName);
        return unit != null && unit->IsVisible;
    }
}
