using System;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using JpEnChat.Chat;

namespace JpEnChat.PartyFinder;

/// <summary>
/// Reads the game's Party Finder listing detail window (<c>LookingForGroupDetail</c>, ClientStructs'
/// <see cref="AddonLookingForGroupDetail"/>). Framework/draw thread only; every pointer is null-checked.
/// </summary>
internal static unsafe class PartyFinderAddon
{
    /// <summary>Addon name of the listing detail window (ClientStructs' <c>[Addon("LookingForGroupDetail")]</c>).</summary>
    public const string Name = "LookingForGroupDetail";

    /// <summary>Reads the listing shown by the detail window at <paramref name="addonPtr"/>; null when the pointer is null.</summary>
    public static PartyFinderListing? Read(nint addonPtr)
    {
        var addon = (AddonLookingForGroupDetail*)addonPtr;
        if (addon == null)
        {
            return null;
        }

        var raw = addon->DescriptionString.AsSpan().ToArray();
        return new PartyFinderListing(
            raw,
            PartyFinderText.Clean(raw),
            ReadText(addon->DutyNameTextNode),
            ReadText(addon->PartyLeaderTextNode));
    }

    /// <summary>
    /// The detail window is visible and still shows the listing with <paramref name="rawDescription"/>. A description
    /// that reads empty (not yet filled in) counts as the same listing.
    /// </summary>
    public static bool IsShowing(ReadOnlySpan<byte> rawDescription)
    {
        var addon = Services.GameGui.GetAddonByName<AddonLookingForGroupDetail>(Name);
        if (addon == null || !addon->AtkUnitBase.IsVisible)
        {
            return false;
        }

        var current = addon->DescriptionString.AsSpan();
        return current.IsEmpty || current.SequenceEqual(rawDescription);
    }

    /// <summary>Display text of a text node: payloads resolved to their visible text, icon glyphs removed.</summary>
    private static string ReadText(AtkTextNode* node)
    {
        if (node == null)
        {
            return string.Empty;
        }

        var text = node->GetText();
        if (!text.HasValue)
        {
            return string.Empty;
        }

        var bytes = text.AsSpan();
        return bytes.IsEmpty ? string.Empty : SeStringText.CleanName(SeString.Parse(bytes).TextValue);
    }
}
