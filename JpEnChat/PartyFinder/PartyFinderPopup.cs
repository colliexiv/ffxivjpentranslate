using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using JpEnChat.Models;
using JpEnChat.Ui;

namespace JpEnChat.PartyFinder;

/// <summary>
/// Small borderless window next to the game's Party Finder listing detail window that shows a listing's description
/// and its translation (PLAN §10).
/// </summary>
/// <remarks>
/// <para>Drawn with a plain <c>ImGui.Begin</c> from the plugin's draw handler, like <see cref="QuickTranslatePopup"/>,
/// so it is not in Dalamud's window list. It takes focus when it appears (so Esc reaches it even if the game would
/// otherwise get the key) and gives it back as soon as the user clicks the game.</para>
/// <para>The translation cell reads the shared <see cref="ChatLine"/> that is also in the log, so streaming, cache hits
/// and retry behave exactly as in the main window.</para>
/// <para>Closes on Close, on Esc (when the popup is focused and no item is active, or when no ImGui text input is active
/// anywhere), when the detail window closes, and when the detail window switches to another listing.</para>
/// </remarks>
internal sealed class PartyFinderPopup : IDisposable
{
    private const string WindowId = "##JpEnChatPartyFinder";
    private const float WidthEm = 28f;
    private const double CopiedSeconds = 1.5;

    private const ImGuiWindowFlags Flags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoDocking
        | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove;

    private static readonly string WarningIcon = FontAwesomeIcon.ExclamationTriangle.ToIconString();

    private readonly Configuration configuration;
    private readonly Action<ChatLine> retry;
    private readonly AxisFont font;

    private PartyFinderListing? listing;
    private ChatLine? line;
    private string header = string.Empty;
    private Vector2 lastSize;
    private double copiedUntil;

    /// <param name="configuration">Settings (font size).</param>
    /// <param name="retry">Re-sends a failed line (<see cref="Translation.TranslationPipeline.Retry"/>); called on the draw thread.</param>
    public PartyFinderPopup(Configuration configuration, Action<ChatLine> retry)
    {
        this.configuration = configuration;
        this.retry = retry;
        font = new AxisFont(configuration);
    }

    public bool IsOpen => listing is not null;

    /// <summary>
    /// Shows <paramref name="shown"/> with the translation in <paramref name="translated"/> (null when the description
    /// is empty), replacing whatever the popup showed before.
    /// </summary>
    public void Show(PartyFinderListing shown, ChatLine? translated)
    {
        ArgumentNullException.ThrowIfNull(shown);
        listing = shown;
        line = translated;
        header = BuildHeader(shown);
        copiedUntil = 0;
    }

    public void Close()
    {
        listing = null;
        line = null;
        header = string.Empty;
    }

    public void Dispose()
    {
        Close();
        font.Dispose();
    }

    /// <summary>Draws the popup if open. Call from the UI draw handler.</summary>
    public void Draw()
    {
        if (listing is not { } shown)
        {
            return;
        }

        if (!IsListingStillShown(shown))
        {
            Close();
            return;
        }

        font.Ensure();
        var scale = ImGuiHelpers.GlobalScale;
        var viewport = ImGuiHelpers.MainViewport;
        var gap = 8f * scale;
        var width = Math.Min(
            Math.Clamp(WidthEm * Math.Clamp(configuration.FontSizePx, 10f, 24f), 300f, 640f) * scale,
            Math.Max(viewport.Size.X - (2f * gap), 100f));
        var size = new Vector2(width, lastSize.Y > 0f ? lastSize.Y : 140f * scale);

        ImGui.SetNextWindowPos(ComputePosition(size, gap), ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0f), new Vector2(width, Math.Max(viewport.Size.Y, 100f)));

        var popupFocused = false;
        var anyItemActive = false;
        var close = false;
        if (ImGui.Begin(WindowId, Flags))
        {
            using (font.Push())
            using (ImRaii.TextWrapPos(0f))
            {
                close = DrawContent(shown);
            }

            lastSize = ImGui.GetWindowSize();
            popupFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
            anyItemActive = ImGui.IsAnyItemActive();
        }

        ImGui.End();

        var keysForUs = (popupFocused && !anyItemActive) || !ImGui.GetIO().WantTextInput;
        if (close || (keysForUs && ImGui.IsKeyPressed(ImGuiKey.Escape, false)))
        {
            Close();
        }
    }

    private static string BuildHeader(PartyFinderListing shown)
    {
        var header = ChatChannels.PartyFinderName;
        if (shown.Duty.Length > 0)
        {
            header += " · " + shown.Duty;
        }

        if (shown.Leader.Length > 0)
        {
            header += " · " + shown.Leader;
        }

        return header;
    }

    private static bool IsListingStillShown(PartyFinderListing shown)
    {
        try
        {
            return PartyFinderAddon.IsShowing(shown.RawDescription);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Draws the popup body. Returns true when Close was clicked.</summary>
    private bool DrawContent(PartyFinderListing shown)
    {
        ImGui.TextColored(ChatChannels.PartyFinderColor, header);

        if (line is not { } translated)
        {
            ImGui.TextDisabled("This listing has no description.");
            ImGui.Spacing();
            return ImGui.SmallButton("Close");
        }

        ImGui.TextUnformatted(shown.Description);
        ImGui.Separator();
        DrawTranslation(translated);
        ImGui.Spacing();

        var canCopy = translated.Status is TranslationStatus.Done or TranslationStatus.CacheHit or TranslationStatus.Corrected
                      && translated.Translation.Length > 0;
        using (ImRaii.Disabled(!canCopy))
        {
            var copied = ImGui.GetTime() < copiedUntil;
            if (ImGui.SmallButton(copied ? "Copied###jpenPfCopy" : "Copy###jpenPfCopy") && canCopy)
            {
                CopyToClipboard(translated.Translation);
            }
        }

        ImGui.SameLine();
        return ImGui.SmallButton("Close");
    }

    private void DrawTranslation(ChatLine translated)
    {
        switch (translated.Status)
        {
            case TranslationStatus.None:
                ImGui.TextDisabled("(not Japanese; not translated)");
                break;

            case TranslationStatus.Pending:
                ImGui.TextDisabled("…");
                break;

            case TranslationStatus.Failed:
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed))
                {
                    using (Services.PluginInterface.UiBuilder.IconFontHandle.Push())
                    {
                        ImGui.TextUnformatted(WarningIcon);
                    }

                    ImGui.SameLine();
                    ImGui.TextUnformatted(translated.Error ?? "error");
                }

                ImGui.SameLine();
                if (ImGui.SmallButton("Retry"))
                {
                    retry(translated);
                }

                break;

            default: // Streaming, Done, CacheHit, Corrected
                ImGui.TextUnformatted(translated.Translation);
                if (translated.Status == TranslationStatus.CacheHit)
                {
                    ImGui.TextDisabled("(from cache)");
                }
                else if (translated.Status == TranslationStatus.Corrected)
                {
                    ImGui.TextDisabled("(corrected by you)");
                }

                break;
        }
    }

    private void CopyToClipboard(string text)
    {
        try
        {
            ImGui.SetClipboardText(text);
            copiedUntil = ImGui.GetTime() + CopiedSeconds;
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"[JpEnChat] clipboard copy failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Right of the detail window, top-aligned; left of it when that would leave the screen; centered when the window's
    /// geometry cannot be read. Then clamped into the main viewport.
    /// </summary>
    private static Vector2 ComputePosition(Vector2 size, float gap)
    {
        var viewport = ImGuiHelpers.MainViewport;
        Vector2 position;
        if (TryGetAddonRect(out var addonPos, out var addonSize))
        {
            position = new Vector2(addonPos.X + addonSize.X + gap, addonPos.Y);
            if (position.X + size.X > viewport.Pos.X + viewport.Size.X)
            {
                position = new Vector2(addonPos.X - gap - size.X, addonPos.Y);
            }
        }
        else
        {
            position = viewport.Pos + ((viewport.Size - size) * 0.5f);
        }

        var max = viewport.Pos + viewport.Size - size;
        return new Vector2(
            Math.Clamp(position.X, viewport.Pos.X, Math.Max(viewport.Pos.X, max.X)),
            Math.Clamp(position.Y, viewport.Pos.Y, Math.Max(viewport.Pos.Y, max.Y)));
    }

    private static bool TryGetAddonRect(out Vector2 position, out Vector2 size)
    {
        try
        {
            return AddonRect.TryGet(PartyFinderAddon.Name, out position, out size);
        }
        catch (Exception)
        {
            position = default;
            size = default;
            return false;
        }
    }
}
