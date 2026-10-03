using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using JpEnChat.Translation;
using JpEnChat.Ui;

namespace JpEnChat.Chat;

/// <summary>A line typed into the game's chat box that was held back for translation.</summary>
/// <param name="ChannelPrefix">Channel command exactly as typed, with trailing whitespace; empty when none was typed.</param>
/// <param name="Body">The English to translate.</param>
/// <param name="RawTyped">The whole line as the chat box would have sent it (restored on cancel).</param>
/// <param name="Kind">Channel for the local log row: the typed command's, else the chat box's selected channel
/// (<see cref="XivChatType.Say"/> when unknown).</param>
/// <param name="TellTarget">Tell target for the row (typed, or the chat box's current tell target); null for non-tells
/// and for <c>/r</c>.</param>
/// <param name="ChannelLabel">Short channel name for the popup header.</param>
public sealed record InterceptedMessage(
    string ChannelPrefix,
    string Body,
    string RawTyped,
    XivChatType Kind,
    string? TellTarget,
    string ChannelLabel);

/// <summary>
/// Hooks <c>UIModule.ProcessChatBoxEntry</c>, the function the game's chat box calls on Enter, so plain English can
/// be translated in a popup before it is sent (PLAN §9).
/// </summary>
/// <remarks>
/// <para><b>Safety rules.</b> (1) Our own sends set <see cref="bypass"/> around the game call and pass straight
/// through. (2) Every decision goes through <see cref="InterceptDecision"/>, which passes anything that is not plainly
/// English chat. (3) Any exception in the detour, or no handler accepting the line, calls the original function, so a
/// bug can never eat a message. (4) If the hook cannot be installed the plugin runs without the feature.</para>
/// <para><b>Signature.</b> <c>void ProcessChatBoxEntry(UIModule* this, Utf8String* message, nint a4, bool
/// saveToHistory)</c> per ClientStructs' <c>[MemberFunction]</c> (<c>UIModule.Delegates.ProcessChatBoxEntry</c>).
/// The managed delegate uses <c>byte</c> for the bool so marshalling reads exactly one byte. The address is
/// <c>UIModule.Addresses.ProcessChatBoxEntry.Value</c>, resolved by ClientStructs' signature scanner at startup.</para>
/// <para>The detour runs on the framework thread (the game's chat box handles Enter there).</para>
/// </remarks>
public sealed unsafe class ChatSendHook : IDisposable
{
    private delegate void ProcessChatBoxEntryDelegate(UIModule* uiModule, Utf8String* message, nint a4, byte saveToHistory);

    private readonly Configuration configuration;
    private readonly IChatSender sender;
    private readonly ILog log;
    private readonly Hook<ProcessChatBoxEntryDelegate>? hook;
    private readonly HashSet<Type> loggedErrors = [];

    private bool bypass;
    private bool disposed;

    public ChatSendHook(Configuration configuration, IChatSender sender, ILog log)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(log);
        this.configuration = configuration;
        this.sender = sender;
        this.log = log;

        try
        {
            var address = UIModule.Addresses.ProcessChatBoxEntry.Value;
            if (address == nint.Zero)
            {
                log.Warning("[JpEnChat] ProcessChatBoxEntry address not resolved; translating from the game's chat box is disabled.");
                return;
            }

            hook = Services.GameInteropProvider.HookFromAddress<ProcessChatBoxEntryDelegate>(address, Detour);
            hook.Enable();
        }
        catch (Exception ex)
        {
            hook?.Dispose();
            hook = null;
            log.Error(ex, "[JpEnChat] could not hook ProcessChatBoxEntry; translating from the game's chat box is disabled.");
        }
    }

    /// <summary>
    /// Takes over a line the decision intercepted; runs on the framework thread inside the game's call. Return
    /// <c>false</c> to let the line be sent unchanged (e.g. the popup is busy sending).
    /// </summary>
    public Func<InterceptedMessage, bool>? OnIntercept { get; set; }

    /// <summary>Whether the hook is installed (false: the feature is unavailable in this session).</summary>
    public bool IsInstalled => hook != null;

    /// <summary>
    /// Sends one line through <see cref="IChatSender"/> without it being intercepted. Framework thread only.
    /// Every send the plugin makes goes through here.
    /// </summary>
    public void SendBypassingHook(string text)
    {
        bypass = true;
        try
        {
            sender.Send(text);
        }
        finally
        {
            bypass = false;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        OnIntercept = null;
        hook?.Disable();
        hook?.Dispose();
    }

    private void Detour(UIModule* uiModule, Utf8String* message, nint a4, byte saveToHistory)
    {
        if (bypass || disposed || message == null || OnIntercept == null)
        {
            hook!.Original(uiModule, message, a4, saveToHistory);
            return;
        }

        string raw;
        InterceptResult decision;
        try
        {
            raw = message->ToString();
            decision = InterceptDecision.Decide(raw, configuration, IsBypassModifierHeld());
        }
        catch (Exception ex)
        {
            LogOnce(ex);
            hook!.Original(uiModule, message, a4, saveToHistory);
            return;
        }

        switch (decision.Action)
        {
            case InterceptAction.PassRewritten:
                SendRewritten(uiModule, message, a4, saveToHistory, decision.Rewritten);
                return;

            case InterceptAction.Intercept:
                bool accepted;
                try
                {
                    accepted = OnIntercept?.Invoke(BuildIntercepted(decision, raw)) ?? false;
                }
                catch (Exception ex)
                {
                    LogOnce(ex);
                    accepted = false;
                }

                if (!accepted)
                {
                    hook!.Original(uiModule, message, a4, saveToHistory);
                    return;
                }

                log.Debug($"[JpEnChat] held a chat-box line for translation ({ChatSendValidation.ChannelPrefix(raw)}, saveToHistory={saveToHistory}).");
                return;

            default:
                hook!.Original(uiModule, message, a4, saveToHistory);
                return;
        }
    }

    /// <summary>Calls the original function with the bypass prefix removed; falls back to the unchanged line on error.</summary>
    private void SendRewritten(UIModule* uiModule, Utf8String* message, nint a4, byte saveToHistory, string rewritten)
    {
        Utf8String* replacement;
        try
        {
            replacement = Utf8String.FromString(rewritten);
        }
        catch (Exception ex)
        {
            LogOnce(ex);
            hook!.Original(uiModule, message, a4, saveToHistory);
            return;
        }

        try
        {
            hook!.Original(uiModule, replacement, a4, saveToHistory);
        }
        finally
        {
            replacement->Dtor(true);
        }
    }

    private bool IsBypassModifierHeld()
    {
        var key = configuration.BypassModifier switch
        {
            BypassModifier.Ctrl => VirtualKey.CONTROL,
            BypassModifier.Shift => VirtualKey.SHIFT,
            BypassModifier.Alt => VirtualKey.MENU,
            _ => (VirtualKey?)null,
        };

        return key is { } k && Services.KeyState[k];
    }

    /// <summary>Adds the channel to show and log: typed command first, else the chat box's selected channel.</summary>
    private static InterceptedMessage BuildIntercepted(InterceptResult decision, string raw)
    {
        if (decision.ChannelKind is { } kind)
        {
            var isReply = kind == XivChatType.TellOutgoing && decision.TellTarget.Length == 0;
            var target = kind == XivChatType.TellOutgoing && !isReply ? decision.TellTarget : null;
            var label = isReply ? "Reply" : ChatChannels.DisplayName(kind);
            return new InterceptedMessage(decision.ChannelPrefix, decision.Body, raw, kind, target, label);
        }

        // No prefix typed: the game sends to the chat box's selected channel.
        XivChatType selected = XivChatType.Say;
        string? tellTarget = null;
        var label2 = "current channel";
        var shell = RaptureShellModule.Instance();
        if (shell != null && OutgoingChannels.FromShellChatType(shell->ChatType) is { } current)
        {
            selected = current;
            label2 = ChatChannels.DisplayName(current);
            if (current == XivChatType.TellOutgoing)
            {
                var name = shell->TellName.ToString();
                var world = shell->TellWorld.ToString();
                tellTarget = world.Length > 0 ? $"{name}@{world}" : name;
                if (name.Length > 0)
                {
                    label2 = $"Tell {tellTarget}";
                }
            }
        }

        return new InterceptedMessage(string.Empty, decision.Body, raw, selected, tellTarget, label2);
    }

    private void LogOnce(Exception ex)
    {
        if (loggedErrors.Add(ex.GetType()))
        {
            log.Error(ex, $"[JpEnChat] chat-box hook error ({ex.GetType().Name}); the line was sent unchanged. Further errors of this type are not logged.");
        }
    }
}
