using System;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using JpEnChat.Chat;
using JpEnChat.Models;
using JpEnChat.Translation;

namespace JpEnChat.Ui;

/// <summary>
/// Small borderless window next to the game's chat box that translates a line intercepted by
/// <see cref="ChatSendHook"/> and sends the Japanese on Enter (PLAN §9).
/// </summary>
/// <remarks>
/// <para>Drawn with a plain <c>ImGui.Begin</c> from the plugin's draw handler (not a <c>Window</c>), so it never shows
/// up in Dalamud's window list and Dalamud's Esc-closes-window handling does not apply. The state machine is
/// <see cref="OutgoingSession"/> and the breakdown panel <see cref="OutgoingPanel"/>.</para>
/// <para><b>Keys.</b> "Popup keys" are read when the popup is focused and none of its inputs is active, or when no
/// ImGui text input is active anywhere (the keyboard belongs to the game), so Esc right after pressing Enter in the
/// game's chat box cancels.</para>
/// <list type="table">
/// <listheader><term>State + key</term><description>Action</description></listheader>
/// <item><term>Translating, Esc</term><description>Cancel; close; put the typed line back into the chat box (or copy it to the clipboard).</description></item>
/// <item><term>Translating, Shift+Enter or "Send English"</term><description>Send the typed line unchanged; close.</description></item>
/// <item><term>Confirming, Enter / Ctrl+Enter in JA box</term><description>Send prefix + Japanese (no prefix typed: the Japanese alone, to the chat box's selected channel); close.</description></item>
/// <item><term>Confirming, Shift+Enter in JA box or "Send English"</term><description>Send the typed line unchanged; close.</description></item>
/// <item><term>Confirming, Esc</term><description>Discard; close; restore the typed line as on cancel.</description></item>
/// <item><term>Confirming, Polite / Casual / Cool / Custom</term><description>Re-translate in that style.</description></item>
/// <item><term>Error shown, Esc</term><description>Close; restore the typed line as on cancel.</description></item>
/// <item><term>Sending, any key</term><description>Ignored until the send completes (success closes; failure shows the error).</description></item>
/// </list>
/// <para>Keyboard focus moves to the JA box only once the translation arrives; while translating the popup does not
/// take focus.</para>
/// </remarks>
internal sealed class QuickTranslatePopup : IDisposable
{
    private const string WindowId = "##JpEnChatQuickTranslate";
    private const float BaseWidth = 440f;
    private const double NoticeSeconds = 2.0;

    private const ImGuiWindowFlags Flags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoDocking
        | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove;

    private readonly Configuration configuration;
    private readonly ChatLog log;
    private readonly Func<string> localPlayerName;
    private readonly OutgoingSession session;
    private readonly OutgoingPanel panel;
    private readonly AxisFont font;

    private InterceptedMessage? current;
    private bool open;
    private bool focusWindow;
    private Vector2 lastSize;
    private string? notice;
    private double closeAt;

    /// <param name="configuration">Settings (default style, custom persona, popup offsets, font size).</param>
    /// <param name="log">Row store; successful sends are added as rows.</param>
    /// <param name="translator">EN→JA structured translation.</param>
    /// <param name="send">Sends one full chat-box line through the hook bypass on the framework thread.</param>
    /// <param name="localPlayerName">Local player name for sent rows.</param>
    /// <param name="context">Builds the game context for a message to a channel (and tell target); null sends none.</param>
    public QuickTranslatePopup(
        Configuration configuration,
        ChatLog log,
        IOutgoingTranslator translator,
        Func<string, Task> send,
        Func<string> localPlayerName,
        Func<XivChatType, string?, TranslationContext?>? context = null)
    {
        this.configuration = configuration;
        this.log = log;
        this.localPlayerName = localPlayerName;
        session = new OutgoingSession(
            translator,
            send,
            FrameworkPost.Run,
            configuration.DefaultStyle,
            () => current?.ChannelPrefix ?? string.Empty,
            () => current?.Body ?? string.Empty,
            () => context is not null && current is { } m ? context(m.Kind, m.TellTarget) : null);
        session.Translated += () => focusWindow = true;
        session.Sent += OnSent;
        panel = new OutgoingPanel(session, "popup")
        {
            ConfirmHint = "Enter: send  Shift+Enter: send English  Esc: cancel",
            ErrorHint = "(Esc to close)",
            ShiftEnter = SendEnglish,
            PrefixByteCount = () => Encoding.UTF8.GetByteCount(current?.ChannelPrefix ?? string.Empty),
            CustomStyleTooltip = () => configuration.CustomStyleText.Trim().Length == 0
                ? "Custom persona is empty (Settings → General); Polite is used."
                : "Your persona: " + configuration.CustomStyleText.Trim(),
        };
        font = new AxisFont(configuration);
    }

    public bool IsOpen => open;

    /// <summary>
    /// Starts translating an intercepted line and opens the popup. Called by the hook on the framework thread.
    /// Returns false (the hook then sends the line unchanged) while a previous send is still in progress.
    /// </summary>
    public bool TryBegin(InterceptedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (session.State == OutgoingState.Sending)
        {
            return false;
        }

        current = message;
        notice = null;
        focusWindow = false;
        session.Reset();
        session.SetStyle(configuration.DefaultStyle); // each message starts in the default style
        session.StartTranslation(message.Body);
        open = true;
        return true;
    }

    /// <summary>Cancels and closes without restoring anything (plugin unload).</summary>
    public void Dispose()
    {
        session.Dispose();
        open = false;
        current = null;
        font.Dispose();
    }

    /// <summary>Draws the popup if open. Call from the UI draw handler.</summary>
    public void Draw()
    {
        if (!open || current is not { } message)
        {
            return;
        }

        if (notice != null && ImGui.GetTime() >= closeAt)
        {
            Close();
            return;
        }

        font.Ensure();
        var scale = ImGuiHelpers.GlobalScale;
        var width = BaseWidth * scale;
        var size = lastSize.Y > 0f ? lastSize : new Vector2(width, 110f * scale);

        ImGui.SetNextWindowPos(ComputePosition(size, scale), ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0f), new Vector2(width, float.MaxValue));

        var popupFocused = false;
        var anyItemActive = false;
        if (ImGui.Begin(WindowId, Flags))
        {
            if (focusWindow)
            {
                ImGui.SetWindowFocus();
                focusWindow = false;
            }

            using (font.Push())
            using (ImRaii.TextWrapPos(0f))
            {
                DrawContent(message);
            }

            lastSize = ImGui.GetWindowSize();
            popupFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
            anyItemActive = ImGui.IsAnyItemActive();
        }

        ImGui.End();

        HandleKeys(popupFocused, anyItemActive);
    }

    private void DrawContent(InterceptedMessage message)
    {
        ImGui.TextDisabled($"EN → JA  ·  {message.ChannelLabel}");
        ImGui.TextUnformatted(message.Body);

        if (notice != null)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, notice);
            return;
        }

        panel.Draw();

        if (session.State is OutgoingState.Translating or OutgoingState.Confirming
            || (session.State == OutgoingState.Editing && session.Error != null))
        {
            if (ImGui.SmallButton("Send English"))
            {
                SendEnglish();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Send what you typed, untranslated (Shift+Enter)");
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel"))
            {
                Cancel();
            }
        }
    }

    private void HandleKeys(bool popupFocused, bool anyItemActive)
    {
        var io = ImGui.GetIO();
        var keysForUs = (popupFocused && !anyItemActive) || !io.WantTextInput;

        var escape = panel.ConsumeEscapeFromInput() || (keysForUs && ImGui.IsKeyPressed(ImGuiKey.Escape, false));
        if (escape)
        {
            Cancel();
            return;
        }

        var enter = ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false);
        if (keysForUs && enter && io.KeyShift
            && session.State is OutgoingState.Translating or OutgoingState.Confirming)
        {
            SendEnglish();
        }
    }

    /// <summary>Sends the line exactly as typed (the bypass path), closing the popup on success.</summary>
    private void SendEnglish()
    {
        if (current is not { } message || session.State == OutgoingState.Sending || notice != null)
        {
            return;
        }

        session.SendAsIs(message.RawTyped, message.Body);
    }

    /// <summary>Esc / Cancel: stop, close, and give the typed line back to the user.</summary>
    private void Cancel()
    {
        if (current is not { } message || session.State == OutgoingState.Sending || notice != null)
        {
            return;
        }

        session.Reset();
        if (RestoreToChatBox(message.RawTyped))
        {
            Close();
            return;
        }

        try
        {
            ImGui.SetClipboardText(message.RawTyped);
            notice = "English copied to clipboard.";
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"[JpEnChat] clipboard copy failed: {ex.GetType().Name}");
            notice = "Could not restore the text.";
        }

        closeAt = ImGui.GetTime() + NoticeSeconds;
    }

    private static bool RestoreToChatBox(string text)
    {
        try
        {
            return ChatLogAddon.TrySetInputText(text);
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"[JpEnChat] could not restore text into the chat box: {ex.GetType().Name}");
            return false;
        }
    }

    private void OnSent(SentMessage sent)
    {
        if (current is { } message)
        {
            log.Add(SentLines.Build(message.Kind, message.TellTarget, sent, localPlayerName()));
        }

        Close();
    }

    private void Close()
    {
        session.Reset();
        open = false;
        current = null;
        notice = null;
        focusWindow = false;
    }

    /// <summary>
    /// Right of the chat window, bottom-aligned; above it when that would leave the screen; centered low on the screen
    /// when the chat window is not visible. Then the configured offsets, then clamped into the main viewport.
    /// </summary>
    private Vector2 ComputePosition(Vector2 size, float scale)
    {
        var viewport = ImGuiHelpers.MainViewport;
        var gap = 8f * scale;
        Vector2 position;
        if (TryGetChatRect(out var chatPos, out var chatSize))
        {
            position = new Vector2(chatPos.X + chatSize.X + gap, chatPos.Y + chatSize.Y - size.Y);
            if (position.X + size.X > viewport.Pos.X + viewport.Size.X)
            {
                position = new Vector2(chatPos.X, chatPos.Y - size.Y - gap);
            }
        }
        else
        {
            position = viewport.Pos + new Vector2((viewport.Size.X - size.X) * 0.5f, viewport.Size.Y * 0.65f);
        }

        position += new Vector2(configuration.PopupOffsetX, configuration.PopupOffsetY) * scale;

        var max = viewport.Pos + viewport.Size - size;
        return new Vector2(
            Math.Clamp(position.X, viewport.Pos.X, Math.Max(viewport.Pos.X, max.X)),
            Math.Clamp(position.Y, viewport.Pos.Y, Math.Max(viewport.Pos.Y, max.Y)));
    }

    private static bool TryGetChatRect(out Vector2 position, out Vector2 size)
    {
        try
        {
            return ChatLogAddon.TryGetRect(out position, out size);
        }
        catch (Exception)
        {
            position = default;
            size = default;
            return false;
        }
    }
}
