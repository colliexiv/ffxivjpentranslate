using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using JpEnChat.Models;

namespace JpEnChat.Windows;

/// <summary>
/// Two-pane chat window: original on the left, translation on the right, input pinned to the bottom (PLAN §4).
/// </summary>
/// <remarks>
/// Phase 1 placeholder: renders a few fake rows and an input box that only echoes locally.
/// TODO(Phase 2B): replace <see cref="lines"/> with the shared chat log store fed by ingest, add per-channel colors,
/// channel filter, auto-scroll, "⚠ retry" cells, hover tooltips, game-font handle at <see cref="Configuration.FontSizePx"/>,
/// and the outgoing breakdown panel driven by <see cref="OutgoingState"/>.
/// </remarks>
public sealed class MainWindow : Window, IDisposable
{
    private const int InputMaxBytes = 500;

    private readonly Configuration configuration;
    private readonly List<ChatLine> lines;
    private string input = string.Empty;

    public MainWindow(Configuration configuration)
        : base("JP/EN Chat###JpEnChatMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.configuration = configuration;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 240),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        // Fake rows so the layout can be checked in-game before the pipeline exists.
        var now = DateTime.Now;
        lines =
        [
            new ChatLine
            {
                Timestamp = now.AddSeconds(-20), Kind = XivChatType.Party, SenderName = "Tanaka Taro",
                SenderWorld = "Gaia", Original = "よろしくお願いします！", OriginalLang = Lang.Ja,
                Status = TranslationStatus.Done, Translation = "Nice to meet you, looking forward to this!",
            },
            new ChatLine
            {
                Timestamp = now.AddSeconds(-5), Kind = XivChatType.Party, SenderName = "Suzuki Hanako",
                SenderWorld = "Gaia", Original = "1ボス行きます", OriginalLang = Lang.Ja,
                Status = TranslationStatus.Pending,
            },
            new ChatLine
            {
                Timestamp = now, Kind = XivChatType.Say, SenderName = "Example Player",
                SenderWorld = "Gaia", Original = "hello!", OriginalLang = Lang.En,
                Status = TranslationStatus.None,
            },
        ];
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var style = ImGui.GetStyle();
        var inputBlockHeight = ImGui.GetFrameHeightWithSpacing() + style.ItemSpacing.Y;

        using (var child = ImRaii.Child("##log", new Vector2(0, -inputBlockHeight), true))
        {
            if (child.Success)
            {
                DrawLog();
            }
        }

        DrawInput();
    }

    private void DrawLog()
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame
                                      | ImGuiTableFlags.Resizable;

        using var table = ImRaii.Table("##logTable", 2, flags);
        if (!table.Success)
        {
            return;
        }

        ImGui.TableSetupColumn("Original", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Translation", ImGuiTableColumnFlags.WidthStretch);

        foreach (var line in lines)
        {
            using var id = ImRaii.PushId(line.Id.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var prefix = configuration.ShowTimestamps ? $"{line.Timestamp:HH:mm} " : string.Empty;
            ImGui.TextWrapped($"{prefix}{ChannelTag(line.Kind)} {line.SenderName}: {line.Original}");

            ImGui.TableNextColumn();
            switch (line.Status)
            {
                case TranslationStatus.Pending:
                    ImGui.TextDisabled("…");
                    break;
                case TranslationStatus.Failed:
                    ImGui.TextColored(ImGuiColors.DalamudRed, $"⚠ {line.Error ?? "failed"}");
                    break;
                case TranslationStatus.None:
                    break;
                default:
                    ImGui.TextWrapped(line.Translation);
                    break;
            }
        }
    }

    private void DrawInput()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("EN>");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);

        if (ImGui.InputText("##en", ref input, InputMaxBytes, ImGuiInputTextFlags.EnterReturnsTrue)
            && !string.IsNullOrWhiteSpace(input))
        {
            // TODO(Phase 2B/3): hand off to the outgoing flow (ITranslator.TranslateOutgoingAsync → confirm →
            // IChatSender.Send on the framework thread). Phase 1 only echoes the text as a local row.
            lines.Add(new ChatLine
            {
                Kind = XivChatType.Say,
                SenderName = "You",
                Original = input.Trim(),
                OriginalLang = Lang.En,
                IsOwn = true,
                IsSentByPlugin = true,
            });
            input = string.Empty;
            ImGui.SetKeyboardFocusHere(-1);
        }
    }

    private static string ChannelTag(XivChatType kind) => kind switch
    {
        XivChatType.Say => "[S]",
        XivChatType.Shout => "[Sh]",
        XivChatType.Yell => "[Y]",
        XivChatType.Party or XivChatType.CrossParty => "[P]",
        XivChatType.Alliance => "[A]",
        XivChatType.FreeCompany => "[FC]",
        XivChatType.TellIncoming => "[From]",
        XivChatType.TellOutgoing => "[To]",
        XivChatType.NoviceNetwork => "[NN]",
        >= XivChatType.Ls1 and <= XivChatType.Ls8 => $"[LS{kind - XivChatType.Ls1 + 1}]",
        XivChatType.CrossLinkShell1 => "[CWLS1]",
        >= XivChatType.CrossLinkShell2 and <= XivChatType.CrossLinkShell8 =>
            $"[CWLS{kind - XivChatType.CrossLinkShell2 + 2}]",
        _ => $"[{kind}]",
    };
}
