using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using JpEnChat.Translation;
using JpEnChat.Ui;

namespace JpEnChat.Windows;

/// <summary>
/// Settings → Translations (PLAN §11): the fixed (pinned) translations with an "Add" row, and the cached translations
/// with search, edit, pin, delete and a two-click "Clear cache".
/// </summary>
/// <remarks>
/// <para>Reads the cache through <see cref="ITranslationCache.Snapshot"/> only when <see cref="ITranslationCache.Version"/>
/// changed (or the search text did), so an idle frame allocates nothing. Both lists are drawn with
/// <c>ImGuiListClipper</c>, so only visible rows cost anything however large the cache is.</para>
/// <para>Every edit goes straight to the cache and is followed by <c>saveCache</c>. Editing a translation (fixed or
/// cached) pins it: a hand-edited translation is a correction and must not be evicted or overwritten. Draw thread
/// only.</para>
/// </remarks>
internal sealed class TranslationsTab(ITranslationCache cache, Action saveCache)
{
    private const int OriginalMaxBytes = 500 * 3;
    private const int TranslationMaxBytes = TranslationEditor.MaxChars * 3;
    private const double ConfirmSeconds = 4.0;

    private static readonly string[] DirectionLabels = ["JA→EN", "EN→JA"];
    private static readonly TranslationDirection[] Directions = [TranslationDirection.JaToEn, TranslationDirection.EnToJa];
    private static readonly string DefaultsTooltip = BuildDefaultsTooltip();

    private readonly TranslationEditor editor = new();
    private readonly List<CacheEntry> fixedEntries = [];
    private readonly List<CacheEntry> cachedEntries = [];
    private readonly List<CacheEntry> filteredCached = [];

    private int seenVersion = -1;
    private string filteredFor = string.Empty;
    private string search = string.Empty;
    private int addDirection;
    private string addOriginal = string.Empty;
    private string addTranslation = string.Empty;
    private string status = string.Empty;
    private bool statusIsError;
    private double clearArmedUntil;

    /// <summary>Forgets transient state (status line, armed "Clear cache", cached lists). Call on window open/close.</summary>
    public void Reset()
    {
        seenVersion = -1;
        status = string.Empty;
        clearArmedUntil = 0;
    }

    public void Draw()
    {
        Refresh();

        DrawFixedSection();
        ImGui.Spacing();
        ImGui.Separator();
        DrawCachedSection();

        if (status.Length > 0)
        {
            ImGui.Spacing();
            if (statusIsError)
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, status);
            }
            else
            {
                ImGui.TextDisabled(status);
            }
        }

        // Outside the tables and the tab's other popups, in the window's ID scope (see TranslationEditor).
        editor.Draw();
    }

    // ---- Fixed translations ----

    private void DrawFixedSection()
    {
        ImGui.TextUnformatted($"Fixed translations ({fixedEntries.Count})");
        ImGui.SameLine();
        if (ImGui.SmallButton("Add defaults"))
        {
            var added = FixedTranslationDefaults.AddTo(cache);
            Changed(added == 0 ? "All default fixed translations are already present." : $"Added {added} default fixed translation(s).");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(DefaultsTooltip);
        }

        ImGui.TextDisabled("Always used for these exact messages, instead of asking the model. Never evicted; kept by Clear cache.");

        DrawEntryTable("##jpenFixed", fixedEntries, TableHeight(Math.Clamp(fixedEntries.Count, 3, 8)), pinnedTable: true);
        DrawAddRow();
    }

    private void DrawAddRow()
    {
        using var id = ImRaii.PushId("##jpenAddFixed");
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var comboWidth = ImGui.CalcTextSize("EN→JA").X + (ImGui.GetFrameHeight() * 1.5f);
        var buttonWidth = ImGui.CalcTextSize("Add").X + (ImGui.GetStyle().FramePadding.X * 2f);
        var fieldWidth = Math.Max((ImGui.GetContentRegionAvail().X - comboWidth - buttonWidth - (spacing * 3f)) / 2f, 60f);

        ImGui.SetNextItemWidth(comboWidth);
        using (var combo = ImRaii.Combo("##dir", DirectionLabels[addDirection]))
        {
            if (combo.Success)
            {
                for (var i = 0; i < DirectionLabels.Length; i++)
                {
                    if (ImGui.Selectable(DirectionLabels[i], i == addDirection))
                    {
                        addDirection = i;
                    }
                }
            }
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(fieldWidth);
        var enter = ImGui.InputTextWithHint("##original", addDirection == 0 ? "Japanese, e.g. ノ" : "English, e.g. o/", ref addOriginal, OriginalMaxBytes, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(fieldWidth);
        enter |= ImGui.InputTextWithHint("##translation", addDirection == 0 ? "English, e.g. o/" : "Japanese, e.g. ノ", ref addTranslation, TranslationMaxBytes, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ImGui.Button("Add") || enter)
        {
            AddFixed();
        }
    }

    private void AddFixed()
    {
        var original = TranslationEditor.Clean(addOriginal);
        var translation = TranslationEditor.Clean(addTranslation);
        if (original.Length == 0 || translation.Length == 0)
        {
            Error("Enter both the original and the translation.");
            return;
        }

        var direction = Directions[addDirection];
        var key = cache.CreateKey(original, direction);
        if (key.NormalizedText.Length == 0)
        {
            Error("That original is empty once normalized.");
            return;
        }

        var replaced = cache.TryGetEntry(key, out var existing) && existing.Pinned;
        cache.Put(key, translation, pinned: true, display: original);
        addOriginal = string.Empty;
        addTranslation = string.Empty;
        Changed(replaced
            ? $"Replaced the fixed translation of \"{original}\"."
            : $"Added a fixed translation for \"{original}\" (matched as \"{key.NormalizedText}\").");
    }

    // ---- Cached translations ----

    private void DrawCachedSection()
    {
        ImGui.TextUnformatted($"Cached translations ({cachedEntries.Count})");
        ImGui.SameLine();

        var armed = ImGui.GetTime() < clearArmedUntil;
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed, armed))
        {
            if (ImGui.SmallButton(armed ? "Click again to confirm###jpenClearCache" : "Clear cache###jpenClearCache"))
            {
                if (armed)
                {
                    var count = cachedEntries.Count;
                    cache.Clear();
                    clearArmedUntil = 0;
                    Changed($"Cleared {count} cached translation(s); fixed translations kept.");
                }
                else
                {
                    clearArmedUntil = ImGui.GetTime() + ConfirmSeconds;
                }
            }
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##jpenCacheSearch", "Search original or translation", ref search, 256);
        if (!string.Equals(search, filteredFor, StringComparison.Ordinal))
        {
            ApplyFilter();
        }

        if (search.Length > 0)
        {
            ImGui.TextDisabled($"{filteredCached.Count} of {cachedEntries.Count} match.");
        }

        // Fill the rest of the tab, leaving room for the status line below.
        var reserve = status.Length > 0 ? ImGui.GetTextLineHeightWithSpacing() * 1.5f : 0f;
        var height = Math.Max(ImGui.GetContentRegionAvail().Y - reserve, TableHeight(5));
        DrawEntryTable("##jpenCached", filteredCached, height, pinnedTable: false);
    }

    // ---- Shared table ----

    /// <summary>Height of a table showing a header and <paramref name="rows"/> rows.</summary>
    private static float TableHeight(int rows) =>
        (ImGui.GetFrameHeight() + (ImGui.GetStyle().CellPadding.Y * 2f)) * (rows + 1);

    private void DrawEntryTable(string id, List<CacheEntry> entries, float height, bool pinnedTable)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV
                                      | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        using var table = ImRaii.Table(id, 4, flags, new Vector2(-1f, height));
        if (!table.Success)
        {
            return;
        }

        var actionsWidth = ImGui.CalcTextSize(pinnedTable ? "Edit Unpin Delete" : "Edit Pin Delete").X
                           + (ImGui.GetStyle().FramePadding.X * 6f) + (ImGui.GetStyle().ItemSpacing.X * 2f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Dir", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("EN→JA").X);
        ImGui.TableSetupColumn("Original", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Translation", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, actionsWidth);
        ImGui.TableHeadersRow();

        if (entries.Count == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(1);
            ImGui.TextDisabled(pinnedTable ? "None yet. Right-click a row in the log, or add one below." : search.Length > 0 ? "No match." : "Empty.");
            return;
        }

        var clipper = ImGui.ImGuiListClipper();
        try
        {
            clipper.Begin(entries.Count);
            while (clipper.Step())
            {
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    DrawEntryRow(i, entries[i], pinnedTable);
                }
            }
        }
        finally
        {
            clipper.Destroy(); // ends the clipper too (its destructor calls End)
        }
    }

    private void DrawEntryRow(int index, CacheEntry entry, bool pinnedTable)
    {
        using var id = ImRaii.PushId(index);
        ImGui.TableNextRow(ImGuiTableRowFlags.None, ImGui.GetFrameHeight());

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(TranslationEditor.DirectionLabel(entry.Direction));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(entry.Original);
        if (ImGui.IsItemHovered())
        {
            DrawEntryTooltip(entry);
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(entry.Translation);
        if (ImGui.IsItemHovered())
        {
            DrawEntryTooltip(entry);
        }

        ImGui.TableNextColumn();
        if (ImGui.SmallButton("Edit"))
        {
            var key = entry.Key;
            var display = entry.Display;
            editor.Open(
                entry.Original,
                entry.Direction,
                entry.Translation,
                pinnedTable ? "Saved as the fixed translation." : "Saving makes this a fixed translation.",
                text =>
                {
                    cache.Put(key, text, pinned: true, display: display);
                    Changed("Saved.");
                });
        }

        ImGui.SameLine();
        if (ImGui.SmallButton(pinnedTable ? "Unpin" : "Pin"))
        {
            cache.SetPinned(entry.Key, !pinnedTable);
            Changed(pinnedTable ? "Unpinned; it is now an ordinary cached translation." : "Pinned as a fixed translation.");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Delete"))
        {
            cache.Remove(entry.Key);
            Changed("Deleted.");
        }
    }

    private static void DrawEntryTooltip(CacheEntry entry)
    {
        using var tooltip = ImRaii.Tooltip();
        using var wrap = ImRaii.TextWrapPos(ImGui.GetFontSize() * 30f);
        ImGui.TextWrapped(entry.Original);
        ImGui.Separator();
        ImGui.TextWrapped(entry.Translation);
        if (!string.Equals(entry.Original, entry.Key.NormalizedText, StringComparison.Ordinal))
        {
            ImGui.Separator();
            ImGui.TextDisabled($"Matched as: {entry.Key.NormalizedText}");
        }
    }

    // ---- State ----

    /// <summary>Re-reads the cache when its version moved.</summary>
    private void Refresh()
    {
        var version = cache.Version;
        if (version == seenVersion)
        {
            return;
        }

        seenVersion = version;
        fixedEntries.Clear();
        cachedEntries.Clear();
        foreach (var e in cache.Snapshot())
        {
            (e.Pinned ? fixedEntries : cachedEntries).Add(e);
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        filteredFor = search;
        filteredCached.Clear();
        var query = search.Trim();
        if (query.Length == 0)
        {
            filteredCached.AddRange(cachedEntries);
            return;
        }

        // Match the text as typed and as normalized (NFKC folds fullwidth/halfwidth forms, as the cache keys do).
        var normalized = TextNormalizer.Normalize(query, TranslationDirection.JaToEn);
        foreach (var e in cachedEntries)
        {
            if (Matches(e, query) || (normalized.Length > 0 && !string.Equals(normalized, query, StringComparison.Ordinal) && Matches(e, normalized)))
            {
                filteredCached.Add(e);
            }
        }
    }

    private static bool Matches(CacheEntry e, string query) =>
        e.Key.NormalizedText.Contains(query, StringComparison.OrdinalIgnoreCase)
        || e.Translation.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (e.Display is { } d && d.Contains(query, StringComparison.OrdinalIgnoreCase));

    private void Changed(string message)
    {
        status = message;
        statusIsError = false;
        saveCache();
    }

    private void Error(string message)
    {
        status = message;
        statusIsError = true;
    }

    private static string BuildDefaultsTooltip()
    {
        var sb = new System.Text.StringBuilder("Adds any missing starter fixed translations (JA→EN):");
        foreach (var (original, translation) in FixedTranslationDefaults.Entries)
        {
            sb.Append('\n').Append(original).Append(" → ").Append(translation);
        }

        return sb.ToString();
    }
}
