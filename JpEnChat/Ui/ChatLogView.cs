using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using JpEnChat.Models;

namespace JpEnChat.Ui;

/// <summary>
/// The scrolling two-column log (original | translation), one table row per <see cref="ChatLine"/> (PLAN §4).
/// </summary>
/// <remarks>
/// <para><b>Clipping.</b> Rows wrap, so heights vary and <c>ImGuiListClipper</c> does not apply. Instead each drawn
/// row's height is measured (distance between consecutive row tops) and cached by line id. Rows outside the visible
/// band are not drawn; each run of them is replaced by one or two empty rows of the summed height (two when the run
/// length is even, so <see cref="ImGuiTableFlags.RowBg"/> stripes keep their parity). Unmeasured rows use a one-line
/// estimate. When the view is at the bottom every row below the view is drawn, so freshly added rows are measured
/// before the auto-scroll lands. The cache is dropped when the font or log width changes.</para>
/// <para><b>Auto-scroll.</b> If the view was at the bottom before drawing, it is pinned to the bottom after drawing
/// (this also follows a streaming translation that grows the last row). Otherwise new rows only light up the
/// "jump to latest" button.</para>
/// </remarks>
internal sealed class ChatLogView
{
    private static readonly string WarningIcon = FontAwesomeIcon.ExclamationTriangle.ToIconString();

    private readonly ChatLog log;
    private readonly Configuration configuration;
    private readonly IReadOnlySet<XivChatType> hiddenChannels;
    private readonly Func<string> currentWorld;
    private readonly Action<ChatLine> retry;

    private readonly Dictionary<long, float> rowHeights = [];
    private readonly List<long> pruneBuffer = [];
    private float layoutWidth = -1f;
    private float layoutFontSize = -1f;
    private long versionSeenAtBottom;
    private bool scrollToBottomRequested;

    public ChatLogView(
        ChatLog log,
        Configuration configuration,
        IReadOnlySet<XivChatType> hiddenChannels,
        Func<string> currentWorld,
        Action<ChatLine> retry)
    {
        this.log = log;
        this.configuration = configuration;
        this.hiddenChannels = hiddenChannels;
        this.currentWorld = currentWorld;
        this.retry = retry;
    }

    /// <summary>Whether the view was scrolled to the bottom at the end of the last drawn frame.</summary>
    public bool IsAtBottom { get; private set; } = true;

    /// <summary>Rows were added while the view was scrolled up.</summary>
    public bool HasUnseenRows => !IsAtBottom && log.Version != versionSeenAtBottom;

    public void RequestScrollToBottom() => scrollToBottomRequested = true;

    /// <summary>Forgets measured row heights (call after anything that changes wrapping, e.g. the filter).</summary>
    public void InvalidateLayout() => rowHeights.Clear();

    /// <summary>Draws the log into a bordered child of <paramref name="size"/>. Call with the log font pushed.</summary>
    public void Draw(Vector2 size)
    {
        using var child = ImRaii.Child("##jpenLog", size, true);
        if (!child.Success)
        {
            return;
        }

        var atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1f;
        var lines = log.Snapshot();

        UpdateLayoutKey(lines);

        var scrollY = ImGui.GetScrollY();
        var viewHeight = ImGui.GetWindowHeight();
        var margin = viewHeight * 0.5f;
        var viewTop = scrollY - margin;
        var viewBottom = atBottom ? float.MaxValue : scrollY + viewHeight + margin;

        DrawTable(lines, viewTop, viewBottom);

        if (atBottom || scrollToBottomRequested)
        {
            ImGui.SetScrollHereY(1f);
            scrollToBottomRequested = false;
            versionSeenAtBottom = log.Version;
            IsAtBottom = true;
        }
        else
        {
            IsAtBottom = false;
        }
    }

    private void UpdateLayoutKey(IReadOnlyList<ChatLine> lines)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var fontSize = ImGui.GetFontSize();
        if (Math.Abs(width - layoutWidth) > 0.5f || Math.Abs(fontSize - layoutFontSize) > 0.01f)
        {
            rowHeights.Clear();
            layoutWidth = width;
            layoutFontSize = fontSize;
        }

        // Drop heights of rows that fell off the front of the bounded log. Ids increase with insertion order.
        if (rowHeights.Count > lines.Count + 256)
        {
            var oldest = lines.Count > 0 ? lines[0].Id : long.MaxValue;
            pruneBuffer.Clear();
            foreach (var id in rowHeights.Keys)
            {
                if (id < oldest)
                {
                    pruneBuffer.Add(id);
                }
            }

            foreach (var id in pruneBuffer)
            {
                rowHeights.Remove(id);
            }
        }
    }

    private void DrawTable(IReadOnlyList<ChatLine> lines, float viewTop, float viewBottom)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg
                                      | ImGuiTableFlags.SizingStretchSame;

        var padY = ImGui.GetStyle().CellPadding.Y;
        var estimate = ImGui.GetTextLineHeight() + (2f * padY);

        // Content-space y where the next row's cell content starts (same space as GetCursorPosY below).
        var y = ImGui.GetCursorPosY() + padY;

        using var table = ImRaii.Table("##jpenLogTable", 2, flags);
        if (!table.Success)
        {
            return;
        }

        ImGui.TableSetupColumn("Original", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Translation", ImGuiTableColumnFlags.WidthStretch);

        var world = currentWorld();
        var measure = new RowMeasure(rowHeights);
        var skipHeight = 0f;
        var skipCount = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.IsPartyFinder ? configuration.PartyFinderHidden : hiddenChannels.Contains(line.Kind))
            {
                continue;
            }

            var height = rowHeights.TryGetValue(line.Id, out var measured) ? measured : estimate;
            if (y + height < viewTop || y > viewBottom)
            {
                skipHeight += height;
                skipCount++;
                y += height;
                continue;
            }

            EmitSkippedRows(ref measure, ref skipHeight, ref skipCount);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var top = ImGui.GetCursorPosY();
            measure.RowStarted(top, line.Id);

            DrawOriginal(line, world);
            ImGui.TableNextColumn();
            DrawTranslation(line);

            y = top + height;
        }

        EmitSkippedRows(ref measure, ref skipHeight, ref skipCount);
    }

    private static void EmitSkippedRows(ref RowMeasure measure, ref float height, ref int count)
    {
        if (count == 0)
        {
            return;
        }

        // Keep RowBg stripe parity: an odd run becomes one row, an even run two rows.
        var rows = count % 2 == 1 ? 1 : 2;
        var each = height / rows;
        for (var r = 0; r < rows; r++)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, each);
            ImGui.TableNextColumn();
            if (r == 0)
            {
                measure.RowStarted(ImGui.GetCursorPosY(), 0);
            }
        }

        height = 0f;
        count = 0;
    }

    private void DrawOriginal(ChatLine line, string world)
    {
        var color = ChatChannels.Color(line);
        var tag = line.Context is { Length: > 0 } context
            ? $"{ChatChannels.Tag(line)} {context} ·"
            : ChatChannels.Tag(line);
        var showWorld = line.SenderWorld.Length > 0 && !string.Equals(line.SenderWorld, world, StringComparison.Ordinal);
        var showTime = configuration.ShowTimestamps;

        if (line.SenderName.Length == 0)
        {
            if (showTime)
            {
                ImGui.TextColoredWrapped(color, $"{line.Timestamp:HH:mm} {tag} {line.Original}");
            }
            else
            {
                ImGui.TextColoredWrapped(color, $"{tag} {line.Original}");
            }
        }
        else if (showWorld)
        {
            if (showTime)
            {
                ImGui.TextColoredWrapped(
                    color, $"{line.Timestamp:HH:mm} {tag} {line.SenderName}@{line.SenderWorld}: {line.Original}");
            }
            else
            {
                ImGui.TextColoredWrapped(color, $"{tag} {line.SenderName}@{line.SenderWorld}: {line.Original}");
            }
        }
        else if (showTime)
        {
            ImGui.TextColoredWrapped(color, $"{line.Timestamp:HH:mm} {tag} {line.SenderName}: {line.Original}");
        }
        else
        {
            ImGui.TextColoredWrapped(color, $"{tag} {line.SenderName}: {line.Original}");
        }
    }

    private void DrawTranslation(ChatLine line)
    {
        var status = line.Status;
        if (status == TranslationStatus.None)
        {
            return;
        }

        using (ImRaii.Group())
        {
            switch (status)
            {
                case TranslationStatus.Pending:
                    ImGui.TextDisabled("…");
                    break;

                case TranslationStatus.Failed:
                    DrawRetry(line);
                    break;

                default: // Streaming, Done, CacheHit
                    if (line.IsSentByPlugin)
                    {
                        ImGui.TextColoredWrapped(ChatChannels.SentColor, line.Translation);
                    }
                    else
                    {
                        ImGui.TextWrapped(line.Translation);
                    }

                    break;
            }
        }

        if (ImGui.IsItemHovered())
        {
            DrawRowTooltip(line, status);
        }
    }

    private void DrawRetry(ChatLine line)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
        using (Services.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            ImGui.TextUnformatted(WarningIcon);
        }

        ImGui.SameLine();
        if (ImGui.Selectable($"retry##retry{line.Id}"))
        {
            retry(line);
        }
    }

    private static void DrawRowTooltip(ChatLine line, TranslationStatus status)
    {
        using var tooltip = ImRaii.Tooltip();
        using var wrap = ImRaii.TextWrapPos(ImGui.GetFontSize() * 30f);
        ImGui.TextWrapped(line.Original);

        if (status == TranslationStatus.Failed && !string.IsNullOrEmpty(line.Error))
        {
            ImGui.Separator();
            ImGui.TextColoredWrapped(ImGuiColors.DalamudRed, line.Error);
            ImGui.TextDisabled("Click retry to translate again.");
        }
        else if (status == TranslationStatus.CacheHit)
        {
            ImGui.TextDisabled("(from cache)");
        }
        else if (line.IsSentByPlugin)
        {
            ImGui.TextDisabled("(sent by JP/EN Chat)");
        }
    }

    /// <summary>Records each row's height as the distance from its top to the next row's top.</summary>
    private struct RowMeasure(Dictionary<long, float> heights)
    {
        private long pendingId;
        private float pendingTop;

        /// <param name="top">Content-space y of the new row's first cell.</param>
        /// <param name="id">Line id of the new row, or 0 for a filler row that must not be measured.</param>
        public void RowStarted(float top, long id)
        {
            if (pendingId != 0)
            {
                heights[pendingId] = top - pendingTop;
            }

            pendingId = id;
            pendingTop = top;
        }
    }
}
