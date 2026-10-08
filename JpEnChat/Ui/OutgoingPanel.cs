using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using JpEnChat.Models;
using JpEnChat.Translation;

namespace JpEnChat.Ui;

/// <summary>
/// Draws an <see cref="OutgoingSession"/>: "Translating..." while a request is in flight, and while confirming the
/// editable Japanese (JA box), the segment glosses, the back-translation and the style selector
/// (Polite / Casual / Cool / Custom); plus any error. Used by the quick-translate popup.
/// </summary>
/// <remarks>
/// Keys handled here: Enter / Ctrl+Enter in the JA box → <see cref="OutgoingSession.Send"/>; Shift+Enter in the JA box
/// → <see cref="ShiftEnter"/> when set (the popup's "send English"), otherwise Send. Esc that deactivates the JA box is
/// reported through <see cref="ConsumeEscapeFromInput"/> (ImGui reverts the text on Esc; the pre-Esc text is kept).
/// The owner decides what Esc does.
/// </remarks>
internal sealed class OutgoingPanel
{
    private const int MaxInputBytes = IChatSender.MaxMessageBytes;

    private static readonly string[] TranslatingFrames = ["Translating", "Translating.", "Translating..", "Translating..."];

    private readonly OutgoingSession session;
    private readonly string idSuffix;

    private bool escapeFromInput;

    /// <param name="session">The state to draw and drive.</param>
    /// <param name="idSuffix">Makes ImGui ids unique per owner (e.g. "main", "popup").</param>
    public OutgoingPanel(OutgoingSession session, string idSuffix)
    {
        this.session = session;
        this.idSuffix = idSuffix;
    }

    /// <summary>Key hint shown under the confirm controls.</summary>
    public string ConfirmHint { get; set; } = "Enter: send   Ctrl+Enter: send edited   Esc: cancel";

    /// <summary>Shown after an error while Editing.</summary>
    public string ErrorHint { get; set; } = "(Esc to dismiss)";

    /// <summary>When set, Shift+Enter in the JA box calls this instead of sending the Japanese.</summary>
    public Action? ShiftEnter { get; set; }

    /// <summary>When set, a <c>bytes/500</c> counter for prefix + Japanese is drawn next to the JA box.</summary>
    public Func<int>? PrefixByteCount { get; set; }

    /// <summary>Tooltip text of the Custom style button (e.g. the persona text).</summary>
    public Func<string>? CustomStyleTooltip { get; set; }

    /// <summary>True once if Esc deactivated the JA box this frame.</summary>
    public bool ConsumeEscapeFromInput()
    {
        var value = escapeFromInput;
        escapeFromInput = false;
        return value;
    }

    /// <summary>Draws the panel content as one group at the cursor; nothing when <see cref="OutgoingSession.PanelVisible"/> is false.</summary>
    public void Draw()
    {
        if (!session.PanelVisible)
        {
            return;
        }

        var state = session.State;
        using (ImRaii.Group())
        {
            switch (state)
            {
                case OutgoingState.Translating:
                    var frame = (int)(ImGui.GetTime() * 3.0) % TranslatingFrames.Length;
                    ImGui.TextDisabled(TranslatingFrames[frame]);
                    ImGui.SameLine();
                    ImGui.TextDisabled("(Esc to cancel)");
                    break;

                case OutgoingState.Sending:
                    ImGui.TextDisabled("Sending...");
                    break;

                case OutgoingState.Confirming:
                    DrawConfirming();
                    break;
            }

            if (session.Error is { } error)
            {
                ImGui.TextColoredWrapped(ImGuiColors.DalamudRed, error);
                if (session.State == OutgoingState.Editing && ErrorHint.Length > 0)
                {
                    ImGui.TextDisabled(ErrorHint);
                }
            }
        }
    }

    private void DrawConfirming()
    {
        // Editable Japanese. Enter or Ctrl+Enter sends what is in this box.
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("JA>");
        ImGui.SameLine();

        var counterWidth = 0f;
        var prefixBytes = PrefixByteCount;
        if (prefixBytes != null)
        {
            counterWidth = ImGui.CalcTextSize("000/500").X + ImGui.GetStyle().ItemSpacing.X;
        }

        ImGui.SetNextItemWidth(Math.Max(ImGui.GetContentRegionAvail().X - counterWidth, ImGui.GetFontSize() * 4f));
        if (session.ConsumeFocusJapanese())
        {
            ImGui.SetKeyboardFocusHere();
        }

        var japanese = session.Japanese;
        var before = japanese;
        var entered = ImGui.InputText(
            $"##jpenJapanese{idSuffix}", ref japanese, MaxInputBytes, ImGuiInputTextFlags.EnterReturnsTrue);
        if (ImGui.IsItemDeactivated() && ImGui.IsKeyDown(ImGuiKey.Escape))
        {
            japanese = before;
            escapeFromInput = true;
        }

        session.Japanese = japanese;

        if (prefixBytes != null)
        {
            ImGui.SameLine();
            var bytes = session.CommandByteCount(prefixBytes());
            var color = bytes > MaxInputBytes ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey;
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(color, $"{bytes}/{MaxInputBytes}");
        }

        if (entered)
        {
            if (ShiftEnter is { } shiftEnter && ImGui.GetIO().KeyShift)
            {
                shiftEnter();
                return;
            }

            session.Send();
        }

        if (session.Draft is { } d)
        {
            DrawSegments(d);

            if (d.BackTranslation.Length > 0)
            {
                ImGui.TextDisabled("back:");
                ImGui.SameLine();
                ImGui.TextWrapped(d.BackTranslation);
            }
        }

        var style = session.Style;
        for (var i = 0; i < Styles.All.Length; i++)
        {
            var option = Styles.All[i];
            if (i > 0)
            {
                ImGui.SameLine();
            }

            if (ImGui.RadioButton($"{Styles.Label(option)}##{idSuffix}", style == option))
            {
                style = option;
            }

            if (option == OutgoingStyle.Custom && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(CustomStyleTooltip?.Invoke() ?? "Your own persona (Settings → General)");
            }
        }

        ImGui.TextDisabled(ConfirmHint);

        // Last, so a re-translate does not change the state halfway through drawing.
        session.SetStyle(style);
    }

    /// <summary>Flows <c>ja (reading) = en</c> glosses left to right, wrapping at the panel width.</summary>
    private static void DrawSegments(OutgoingDraft d)
    {
        if (d.Segments.Count == 0)
        {
            return;
        }

        var spacing = ImGui.GetStyle().ItemSpacing.X * 2f;
        var available = ImGui.GetContentRegionAvail().X;
        var used = 0f;

        for (var i = 0; i < d.Segments.Count; i++)
        {
            var s = d.Segments[i];
            var showReading = s.Reading.Length > 0 && !string.Equals(s.Reading, s.Ja, StringComparison.Ordinal);
            var jaWidth = ImGui.CalcTextSize(s.Ja).X;
            var glossWidth = showReading
                ? ImGui.CalcTextSize($"({s.Reading}) = {s.En}").X
                : ImGui.CalcTextSize($"= {s.En}").X;
            var width = jaWidth + ImGui.GetStyle().ItemInnerSpacing.X + glossWidth;

            if (i > 0 && used + spacing + width <= available)
            {
                ImGui.SameLine(0f, spacing);
                used += spacing + width;
            }
            else
            {
                used = width;
            }

            ImGui.TextUnformatted(s.Ja);
            ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
            if (showReading)
            {
                ImGui.TextDisabled($"({s.Reading}) = {s.En}");
            }
            else
            {
                ImGui.TextDisabled($"= {s.En}");
            }
        }
    }
}
