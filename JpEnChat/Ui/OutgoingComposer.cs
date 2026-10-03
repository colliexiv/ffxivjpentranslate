using System;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using JpEnChat.Models;
using JpEnChat.Translation;

namespace JpEnChat.Ui;

/// <summary>
/// Bottom input row plus the breakdown panel above it: the outgoing EN→JA flow of PLAN §4.1.
/// </summary>
/// <remarks>
/// <para><b>States</b> (<see cref="OutgoingState"/>): Editing → Translating → Confirming → Sending → Editing.</para>
/// <para><b>Keys.</b> "EN box" is the English input on the bottom row, "JA box" is the editable Japanese in the
/// breakdown panel. Enter and Ctrl+Enter are read from <c>InputText(EnterReturnsTrue)</c> (Ctrl via
/// <c>io.KeyCtrl</c>); Esc is handled once per frame, either when an input box was deactivated by Esc (ImGui
/// reverts the text, so the pre-Esc text is restored) or when the window is focused and Esc is pressed.</para>
/// <list type="table">
/// <listheader><term>State + key</term><description>Action</description></listheader>
/// <item><term>Editing, Enter in EN box</term><description>Parse a typed channel prefix; if text remains, translate (→ Translating).</description></item>
/// <item><term>Editing, Esc</term><description>Dismiss the last error, keep text.</description></item>
/// <item><term>Translating, Enter in EN box</term><description>If the English changed, cancel and re-translate; else ignore.</description></item>
/// <item><term>Translating, Esc</term><description>Cancel the request (→ Editing), keep the English.</description></item>
/// <item><term>Confirming, Enter / Ctrl+Enter in JA box</term><description>Send prefix + JA as edited (→ Sending).</description></item>
/// <item><term>Confirming, Ctrl+Enter in EN box</term><description>Send the current JA without re-translating.</description></item>
/// <item><term>Confirming, Enter in EN box</term><description>English changed since the translation: re-translate. Unchanged: send (so "Enter, Enter" sends even if focus stayed in the EN box).</description></item>
/// <item><term>Confirming, Esc</term><description>Discard the translation (→ Editing), keep the English.</description></item>
/// <item><term>Confirming, register toggle</term><description>Re-translate the EN box text with the new register.</description></item>
/// <item><term>Sending, any key</term><description>Ignored until the send completes (→ Editing, or back to Confirming with the error).</description></item>
/// </list>
/// <para><b>Structure.</b> The state machine lives in <see cref="OutgoingSession"/> and the breakdown panel is drawn
/// by <see cref="OutgoingPanel"/>; both are shared with <see cref="QuickTranslatePopup"/>. This class owns the EN box,
/// the channel combo and the tell target, and maps keys to session calls.</para>
/// <para><b>Threading.</b> All fields are touched only on the draw/framework thread. The translator runs in
/// <c>Task.Run</c>; its result is marshalled back with <c>IFramework.RunOnFrameworkThread</c> and dropped if a newer
/// request or a cancel happened in between (generation counter).</para>
/// </remarks>
internal sealed class OutgoingComposer : IDisposable
{
    private const int MaxInputBytes = IChatSender.MaxMessageBytes;
    private const int MaxTellTargetBytes = 64;

    private readonly ChatLog log;
    private readonly Func<string> localPlayerName;
    private readonly OutgoingSession session;
    private readonly OutgoingPanel panel;

    private string english = string.Empty;
    private string tellTarget = string.Empty;
    private int channelIndex = OutgoingChannels.SayIndex;

    private bool focusEnglish;
    private bool escapeFromInput;

    public OutgoingComposer(
        Configuration configuration,
        ChatLog log,
        IOutgoingTranslator translator,
        Func<string, Task> send,
        Func<string> localPlayerName)
    {
        this.log = log;
        this.localPlayerName = localPlayerName;
        session = new OutgoingSession(
            translator,
            send,
            PostToFramework,
            configuration.DefaultRegister,
            () => OutgoingChannels.Prefix(Channel, tellTarget),
            () => english);
        session.TranslationFailed += () => focusEnglish = true;
        session.Sent += OnSent;
        panel = new OutgoingPanel(session, "main");
    }

    public OutgoingState State => session.State;

    private OutgoingChannel Channel => OutgoingChannels.All[channelIndex];

    /// <summary>Height the panel will take this frame (last frame's measurement), including spacing; 0 when hidden.</summary>
    public float ReservedPanelHeight()
    {
        if (!session.PanelVisible)
        {
            return 0f;
        }

        var state = session.State;
        var estimate = ImGui.GetFrameHeightWithSpacing() * (state == OutgoingState.Confirming ? 4f : 1f);
        // Separator (1 px) + spacing above and below it + the panel group.
        var measured = panel.MeasuredHeight(state);
        return (measured > 0f ? measured : estimate) + (ImGui.GetStyle().ItemSpacing.Y * 2f) + 1f;
    }

    /// <summary>Height of the input row.</summary>
    public static float InputRowHeight() => ImGui.GetFrameHeightWithSpacing();

    public void Dispose() => session.Dispose();

    /// <summary>Draws the breakdown panel (if any). Call directly above <see cref="DrawInputRow"/>.</summary>
    public void DrawPanel()
    {
        if (!session.PanelVisible)
        {
            panel.ResetMeasurement();
            return;
        }

        ImGui.Separator();
        panel.Draw();
    }

    /// <summary>Draws <c>EN&gt; [channel] [tell target] [english............] n/500</c>.</summary>
    public void DrawInputRow()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("EN>");
        ImGui.SameLine();

        DrawChannelCombo();

        if (Channel.IsTell)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10f);
            ImGui.InputTextWithHint("##jpenTellTarget", "Name Surname@World", ref tellTarget, MaxTellTargetBytes);
        }

        ImGui.SameLine();

        var showCounter = session.State == OutgoingState.Confirming;
        var counterWidth = showCounter
            ? ImGui.CalcTextSize("000/500").X + ImGui.GetStyle().ItemSpacing.X
            : 0f;
        ImGui.SetNextItemWidth(Math.Max(ImGui.GetContentRegionAvail().X - counterWidth, ImGui.GetFontSize() * 4f));

        if (focusEnglish)
        {
            ImGui.SetKeyboardFocusHere();
            focusEnglish = false;
        }

        var hint = session.State switch
        {
            OutgoingState.Confirming => "Enter: send  |  edit + Enter: re-translate  |  Esc: cancel",
            OutgoingState.Translating => "Esc: cancel",
            _ => "Type English (or /p, /fc, /l1 ... prefix), Enter to translate",
        };

        var before = english;
        var entered = ImGui.InputTextWithHint(
            "##jpenEnglish", hint, ref english, MaxInputBytes, ImGuiInputTextFlags.EnterReturnsTrue);
        if (ImGui.IsItemDeactivated() && ImGui.IsKeyDown(ImGuiKey.Escape))
        {
            english = before; // ImGui reverts to the text at activation on Esc; keep what the user typed.
            escapeFromInput = true;
        }

        if (entered)
        {
            OnEnglishEnter(ImGui.GetIO().KeyCtrl);
        }

        if (showCounter)
        {
            ImGui.SameLine();
            var bytes = session.CommandByteCount(OutgoingChannels.PrefixByteCount(Channel, tellTarget));
            var color = bytes > MaxInputBytes ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey;
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(color, $"{bytes}/{MaxInputBytes}");
        }
    }

    /// <summary>Runs the Esc transition at most once per frame. Call after drawing the panel and input row.</summary>
    public void EndFrame(bool windowFocused)
    {
        var fromPanel = panel.ConsumeEscapeFromInput();
        var escape = escapeFromInput || fromPanel || (windowFocused && ImGui.IsKeyPressed(ImGuiKey.Escape, false));
        escapeFromInput = false;
        if (!escape)
        {
            return;
        }

        var state = session.State;
        session.Escape();
        if (state is OutgoingState.Translating or OutgoingState.Confirming)
        {
            focusEnglish = true;
        }
    }

    private void DrawChannelCombo()
    {
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6.5f);
        using var combo = ImRaii.Combo("##jpenChannel", Channel.Label);
        if (!combo.Success)
        {
            return;
        }

        for (var i = 0; i < OutgoingChannels.All.Count; i++)
        {
            var selected = i == channelIndex;
            if (ImGui.Selectable(OutgoingChannels.All[i].Label, selected))
            {
                channelIndex = i;
                focusEnglish = true;
            }

            if (selected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }
    }

    private void OnEnglishEnter(bool ctrl)
    {
        // Keep typing focus after Enter, except Enter on an empty box while editing, which hands the keyboard back
        // to the game like the vanilla chat box does.
        focusEnglish = session.State != OutgoingState.Editing || english.Trim().Length > 0;
        ApplyTypedPrefix();
        var text = english.Trim();

        switch (session.State)
        {
            case OutgoingState.Editing:
                if (text.Length > 0)
                {
                    session.StartTranslation(text);
                }

                break;

            case OutgoingState.Translating:
                if (text.Length > 0 && !string.Equals(text, session.RequestedEnglish, StringComparison.Ordinal))
                {
                    session.StartTranslation(text);
                }

                break;

            case OutgoingState.Confirming:
                if (ctrl || text.Length == 0 || string.Equals(text, session.Draft?.EnglishText, StringComparison.Ordinal))
                {
                    session.Send();
                }
                else
                {
                    session.StartTranslation(text);
                }

                break;

            case OutgoingState.Sending:
                break;
        }
    }

    /// <summary>"/p hello" → channel Party, text "hello". Unknown commands are left as text.</summary>
    private void ApplyTypedPrefix()
    {
        if (!OutgoingChannels.TryParsePrefix(english, out var index, out var target, out var body))
        {
            return;
        }

        channelIndex = index;
        if (OutgoingChannels.All[index].IsTell && target.Length > 0)
        {
            tellTarget = target;
        }

        english = body;
    }

    private void OnSent(SentMessage sent)
    {
        log.Add(BuildSentLine(sent));
        english = string.Empty;
        focusEnglish = true;
    }

    /// <summary>The local row for a line sent from this composer; the channel and tell target come from the command.</summary>
    private ChatLine BuildSentLine(SentMessage sent)
    {
        var channel = Channel;
        var target = tellTarget.Trim();
        if (OutgoingChannels.TryParsePrefix(sent.Command, out var index, out var parsedTarget, out _))
        {
            channel = OutgoingChannels.All[index];
            if (parsedTarget.Length > 0)
            {
                target = parsedTarget; // else a same-world target without '@': keep the box's value
            }
        }

        return SentLines.Build(channel.Kind, channel.IsTell ? target : null, sent, localPlayerName());
    }

    internal static void PostToFramework(Action action)
    {
        try
        {
            _ = Services.Framework.RunOnFrameworkThread(action);
        }
        catch (Exception ex)
        {
            // Plugin unloading; nothing left to update.
            Services.Log.Debug($"Dropped outgoing UI update: {ex.GetType().Name}");
        }
    }
}

/// <summary>Builds the log row for a line the plugin sent (left: English, right: Japanese).</summary>
internal static class SentLines
{
    /// <param name="kind">Channel of the row (colors, filter).</param>
    /// <param name="tellTarget">For tells: <c>Name Surname@World</c> (or as typed); the row shows the target as sender.
    /// <c>null</c> for other channels (the row shows the local player).</param>
    /// <param name="sent">What was sent.</param>
    /// <param name="localPlayerName">Local player name; "You" when empty.</param>
    public static ChatLine Build(Dalamud.Game.Text.XivChatType kind, string? tellTarget, SentMessage sent, string localPlayerName)
    {
        string name;
        var world = string.Empty;
        if (tellTarget != null)
        {
            var at = tellTarget.IndexOf('@');
            name = at < 0 ? tellTarget : tellTarget[..at];
            world = at < 0 ? string.Empty : tellTarget[(at + 1)..];
        }
        else
        {
            name = localPlayerName.Length == 0 ? "You" : localPlayerName;
        }

        return new ChatLine
        {
            Kind = kind,
            SenderName = name,
            SenderWorld = world,
            Original = sent.English,
            OriginalLang = Lang.En,
            Translation = sent.Japanese,
            Status = sent.Translated ? TranslationStatus.Done : TranslationStatus.None,
            IsOwn = true,
            IsSentByPlugin = true,
        };
    }
}
