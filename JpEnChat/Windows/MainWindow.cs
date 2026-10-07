using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface;
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
/// Two-pane chat window (PLAN §4). Top to bottom: optional no-key hint, toolbar, log, breakdown panel, input row.
/// </summary>
/// <remarks>
/// The log child's height is whatever is left after reserving the panel (last frame's measured height) and the input
/// row, so the panel pushes the log up instead of covering it. The log, panel and input use the game's Axis font at
/// <see cref="Configuration.FontSizePx"/>; the toolbar keeps Dalamud's default font so icon buttons line up.
/// Esc never closes this window (<see cref="Window.RespectCloseHotkey"/> is off); it drives the outgoing flow instead.
/// </remarks>
public sealed class MainWindow : Window, IDisposable
{
    private const string FilterPopupId = "##jpenChannelFilter";

    private static readonly string CogIcon = FontAwesomeIcon.Cog.ToIconString();

    private readonly Configuration configuration;
    private readonly ChatLog log;
    private readonly Action openConfig;
    private readonly HashSet<XivChatType> hiddenChannels;
    private readonly ChatLogView logView;
    private readonly OutgoingComposer composer;

    private readonly AxisFont font;

    /// <param name="configuration">Shared settings; read every frame (font size, timestamps, filter).</param>
    /// <param name="log">Row store; the window reads it and the send path appends to it.</param>
    /// <param name="translator">EN→JA structured translation for the outgoing flow.</param>
    /// <param name="send">
    /// Sends one full chat command (prefix + Japanese). Must perform the game call on the framework thread
    /// (e.g. <c>text =&gt; framework.RunOnFrameworkThread(() =&gt; chatSender.Send(text))</c>). A faulted task's
    /// message is shown in the panel.
    /// </param>
    /// <param name="corrections">Retry, edit and pin actions of the log's rows (called on the draw thread).</param>
    /// <param name="currentWorld">Local player's current world name; the sender's world is shown only when it differs.</param>
    /// <param name="localPlayerName">Local player name for rows the plugin sent; empty when unknown.</param>
    /// <param name="openConfig">Opens the settings window.</param>
    public MainWindow(
        Configuration configuration,
        ChatLog log,
        IOutgoingTranslator translator,
        Func<string, Task> send,
        ITranslationCorrections corrections,
        Func<string> currentWorld,
        Func<string> localPlayerName,
        Action openConfig)
        : base("JP/EN Chat###JpEnChatMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.configuration = configuration;
        this.log = log;
        this.openConfig = openConfig;

        hiddenChannels = [.. configuration.HiddenLogChannels];
        logView = new ChatLogView(log, configuration, hiddenChannels, currentWorld, corrections);
        composer = new OutgoingComposer(configuration, log, translator, send, localPlayerName);
        font = new AxisFont(configuration);

        RespectCloseHotkey = false;
        Size = new Vector2(640, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 260),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
        composer.Dispose();
        font.Dispose();
    }

    public override void OnOpen()
    {
        // Do not grab keyboard focus here: an active input box blocks game keys. Click the input (or press Enter
        // after a send) to type.
        logView.RequestScrollToBottom();
    }

    public override void Draw()
    {
        if (font.Ensure())
        {
            logView.InvalidateLayout();
        }

        if (configuration.OpenRouterKeyProtected.Length == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Set your OpenRouter key in /jpchat config");
        }

        DrawToolbar();

        using (font.Push())
        {
            var reserved = composer.ReservedPanelHeight() + OutgoingComposer.InputRowHeight();
            var logHeight = Math.Max(ImGui.GetContentRegionAvail().Y - reserved, 40f * ImGuiHelpers.GlobalScale);

            logView.Draw(new Vector2(0f, logHeight));
            composer.DrawPanel();
            composer.DrawInputRow();
        }

        composer.EndFrame(ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows));
    }

    private void DrawToolbar()
    {
        var hiddenCount = hiddenChannels.Count + (configuration.PartyFinderHidden ? 1 : 0);
        if (hiddenCount == 0 ? ImGui.Button("Channels###jpenChannels") : ImGui.Button($"Channels ({hiddenCount} hidden)###jpenChannels"))
        {
            ImGui.OpenPopup(FilterPopupId);
        }

        DrawFilterPopup();

        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##jpenClear", FontAwesomeIcon.TrashAlt))
        {
            log.Clear();
            logView.InvalidateLayout();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Clear the log");
        }

        if (!logView.IsAtBottom)
        {
            ImGui.SameLine();
            var unseen = logView.HasUnseenRows;
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ArrowDown, unseen ? "New messages" : "Latest"))
            {
                logView.RequestScrollToBottom();
            }
        }

        ImGui.SameLine();
        var gearWidth = GearButtonWidth();
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - gearWidth;
        if (right > ImGui.GetCursorPosX())
        {
            ImGui.SetCursorPosX(right);
        }

        if (ImGuiComponents.IconButton("##jpenSettings", FontAwesomeIcon.Cog))
        {
            openConfig();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Settings");
        }
    }

    private void DrawFilterPopup()
    {
        using var popup = ImRaii.Popup(FilterPopupId);
        if (!popup.Success)
        {
            return;
        }

        var changed = false;
        if (ImGui.Button("All"))
        {
            hiddenChannels.Clear();
            configuration.PartyFinderHidden = false;
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("None"))
        {
            foreach (var kind in FilterableChannels())
            {
                hiddenChannels.Add(kind);
            }

            configuration.PartyFinderHidden = true;
            changed = true;
        }

        ImGui.Separator();

        foreach (var kind in FilterableChannels())
        {
            var visible = !hiddenChannels.Contains(kind);
            using var color = ImRaii.PushColor(ImGuiCol.Text, ChatChannels.Color(kind));
            if (ImGui.Checkbox(ChatChannels.DisplayName(kind), ref visible))
            {
                if (visible)
                {
                    hiddenChannels.Remove(kind);
                }
                else
                {
                    hiddenChannels.Add(kind);
                }

                changed = true;
            }
        }

        // Party Finder listing translations are not a chat channel; they have their own flag.
        var partyFinderVisible = !configuration.PartyFinderHidden;
        using (ImRaii.PushColor(ImGuiCol.Text, ChatChannels.PartyFinderColor))
        {
            if (ImGui.Checkbox(ChatChannels.PartyFinderName, ref partyFinderVisible))
            {
                configuration.PartyFinderHidden = !partyFinderVisible;
                changed = true;
            }
        }

        if (changed)
        {
            configuration.HiddenLogChannels = [.. hiddenChannels];
            configuration.Save();
        }
    }

    /// <summary>Captured channels plus Echo (used by the outgoing "Echo (test)" channel).</summary>
    private IEnumerable<XivChatType> FilterableChannels()
    {
        foreach (var kind in configuration.EnabledChannels)
        {
            yield return kind;
        }

        if (!configuration.EnabledChannels.Contains(XivChatType.Echo))
        {
            yield return XivChatType.Echo;
        }
    }

    private static float GearButtonWidth()
    {
        using (Services.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            return ImGui.CalcTextSize(CogIcon).X + (ImGui.GetStyle().FramePadding.X * 2f);
        }
    }
}
