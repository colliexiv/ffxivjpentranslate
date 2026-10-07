using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using JpEnChat.Translation;

namespace JpEnChat.Ui;

/// <summary>
/// Small modal for correcting one translation (PLAN §11): the original (read-only, wrapped) and an editable
/// translation. Shared by the log's right-click menu and Settings → Translations.
/// </summary>
/// <remarks>
/// <para>Keys: Enter or Ctrl+Enter saves, Esc cancels. Newlines are not kept (a translation is one chat line), so
/// Ctrl+Enter, which ImGui would turn into a newline with <see cref="ImGuiInputTextFlags.CtrlEnterForNewLine"/>, saves
/// too. Saving blank text is refused.</para>
/// <para><see cref="Open"/> only records a request; <see cref="Draw"/> opens and draws the modal. Call
/// <see cref="Draw"/> every frame from the same ID scope (same window, outside tables and other popups), because
/// ImGui matches <c>OpenPopup</c> and <c>BeginPopupModal</c> by ID. Draw thread only (the framework thread in Dalamud).</para>
/// </remarks>
internal sealed class TranslationEditor
{
    /// <summary>Longest translation kept, in characters.</summary>
    public const int MaxChars = 2000;

    // InputText limits bytes; three UTF-8 bytes per Japanese character.
    private const int MaxBytes = MaxChars * 3;
    private const string PopupId = "Edit translation###jpenTranslationEditor";

    private bool openRequested;
    private bool focusInput;
    private string original = string.Empty;
    private string header = string.Empty;
    private string note = string.Empty;
    private string buffer = string.Empty;
    private Action<string>? onSave;

    /// <summary>True while the modal is open (or about to open).</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Requests the modal for the next <see cref="Draw"/>.</summary>
    /// <param name="original">Source text shown read-only.</param>
    /// <param name="direction">Shown as JA→EN / EN→JA above the original.</param>
    /// <param name="current">Initial translation text.</param>
    /// <param name="saveNote">Short line under the editor explaining what saving does.</param>
    /// <param name="save">Receives the trimmed, single-line, non-empty translation.</param>
    public void Open(string original, TranslationDirection direction, string current, string saveNote, Action<string> save)
    {
        this.original = original;
        header = DirectionLabel(direction) + " · original";
        note = saveNote;
        buffer = OneLine(current);
        onSave = save;
        openRequested = true;
        focusInput = true;
        IsOpen = true;
    }

    /// <summary>"JA→EN" or "EN→JA".</summary>
    public static string DirectionLabel(TranslationDirection direction) =>
        direction == TranslationDirection.EnToJa ? "EN→JA" : "JA→EN";

    /// <summary>Opens the modal when requested and draws it while open.</summary>
    public void Draw()
    {
        if (openRequested)
        {
            ImGui.OpenPopup(PopupId);
            openRequested = false;
        }

        if (!IsOpen)
        {
            return;
        }

        var width = ImGui.GetFontSize() * 28f;
        ImGui.SetNextWindowSize(new Vector2(width, 0f), ImGuiCond.Appearing);
        using var popup = ImRaii.PopupModal(PopupId, ImGuiWindowFlags.NoSavedSettings);
        if (!popup.Success)
        {
            IsOpen = false; // closed by ImGui (e.g. the window it belonged to went away)
            onSave = null;
            return;
        }

        ImGui.TextDisabled(header);
        ImGui.TextWrapped(original);
        ImGui.Separator();
        ImGui.TextDisabled("Translation");

        if (focusInput)
        {
            ImGui.SetKeyboardFocusHere();
            focusInput = false;
        }

        var entered = ImGui.InputTextMultiline(
            "##jpenEditTranslation",
            ref buffer,
            MaxBytes,
            new Vector2(-1f, ImGui.GetTextLineHeightWithSpacing() * 4f),
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CtrlEnterForNewLine);
        var active = ImGui.IsItemActive() || ImGui.IsItemFocused();
        var ctrlEnter = active && ImGui.GetIO().KeyCtrl
                        && (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false));
        var escape = ImGui.IsKeyPressed(ImGuiKey.Escape, false);

        if (note.Length > 0)
        {
            ImGui.TextDisabled(note);
        }

        var text = Clean(buffer);
        var save = false;
        using (ImRaii.Disabled(text.Length == 0))
        {
            save = ImGui.Button("Save");
        }

        ImGui.SameLine();
        var cancel = ImGui.Button("Cancel");
        ImGui.SameLine();
        ImGui.TextDisabled("Enter: save · Esc: cancel");

        if ((save || entered || ctrlEnter) && text.Length > 0)
        {
            onSave?.Invoke(text);
            Close();
        }
        else if (cancel || escape)
        {
            Close();
        }
    }

    /// <summary>Single line, trimmed, at most <see cref="MaxChars"/> characters.</summary>
    public static string Clean(string text)
    {
        var s = OneLine(text).Trim();
        return s.Length > MaxChars ? s[..MaxChars].TrimEnd() : s;
    }

    private static string OneLine(string s) =>
        s.Contains('\n') || s.Contains('\r') ? s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ') : s;

    private void Close()
    {
        ImGui.CloseCurrentPopup();
        IsOpen = false;
        onSave = null;
        buffer = string.Empty;
    }
}
