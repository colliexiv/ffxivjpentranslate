using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.Shell;
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

/// <summary>Which address <see cref="ChatSendHook"/> hooked (logged at plugin load).</summary>
internal enum ChatInputHookSource
{
    /// <summary>Neither address resolved; the feature is disabled.</summary>
    None,

    /// <summary>ClientStructs' <c>ExecuteCommandInner</c> and the chat-box call target are the same function.</summary>
    Both,

    /// <summary>Both resolved but differ; the chat-box call target (proven against keyboard input) was hooked.</summary>
    CallSiteDiffers,

    /// <summary>Only ClientStructs' <c>ExecuteCommandInner</c> resolved.</summary>
    ClientStructsOnly,

    /// <summary>Only the chat-box call target resolved.</summary>
    CallSiteOnly,
}

/// <summary>Chooses the chat-input hook address from the two candidates (pure, PLAN §9.1).</summary>
internal static class ChatInputHookTarget
{
    /// <summary>
    /// Both nonzero and equal: hook it. Both nonzero and different: hook the chat-box call target. One nonzero: hook
    /// that one. Neither: nothing (<see cref="nint.Zero"/>). Only ever one address, so at most one hook is installed.
    /// </summary>
    /// <param name="clientStructs"><c>ShellCommandModule.Addresses.ExecuteCommandInner.Value</c>, 0 if unresolved.</param>
    /// <param name="callSiteTarget">Destination of the chat box's call, 0 if the signature was not found.</param>
    public static (nint Address, ChatInputHookSource Source) Choose(nint clientStructs, nint callSiteTarget)
    {
        if (clientStructs != nint.Zero && callSiteTarget != nint.Zero)
        {
            return clientStructs == callSiteTarget
                ? (callSiteTarget, ChatInputHookSource.Both)
                : (callSiteTarget, ChatInputHookSource.CallSiteDiffers);
        }

        if (callSiteTarget != nint.Zero)
        {
            return (callSiteTarget, ChatInputHookSource.CallSiteOnly);
        }

        if (clientStructs != nint.Zero)
        {
            return (clientStructs, ChatInputHookSource.ClientStructsOnly);
        }

        return (nint.Zero, ChatInputHookSource.None);
    }
}

/// <summary>
/// Hooks the shell's chat-input processor, the function the game's chat box calls on Enter for every line (plain
/// text and commands alike), so plain English can be translated in a popup before it is sent (PLAN §9).
/// </summary>
/// <remarks>
/// <para><b>Safety rules.</b> (1) Our own sends set <see cref="bypass"/> around the game call and pass straight
/// through. (2) Every decision goes through <see cref="InterceptDecision"/>, which passes anything that is not plainly
/// English chat; lines run by a macro always pass. (3) Any exception in the detour, or no handler accepting the line,
/// calls the original function, so a bug can never eat a message. (4) If the hook cannot be installed the plugin runs
/// without the feature.</para>
/// <para><b>Target.</b> <c>void (ShellCommandModule* self, Utf8String* message, UIModule* uiModule)</c>, ClientStructs'
/// <c>ShellCommandModule.ExecuteCommandInner</c> (<c>ShellCommandModule.Delegates.ExecuteCommandInner</c>). Not
/// <c>UIModule.ProcessChatBoxEntry</c>: that is only the entry point plugins call to send chat, and the game's chat
/// box does not go through it, so a hook there never sees typed text. <c>ProcessChatBoxEntry</c> itself ends up in
/// this function, which is why our own sends re-enter the detour (covered by <see cref="bypass"/>).</para>
/// <para><b>Address.</b> Two candidates are resolved at construction: ClientStructs'
/// <c>ShellCommandModule.Addresses.ExecuteCommandInner.Value</c>, and the destination of the call at the chat box's
/// call site <c>E8 ?? ?? ?? ?? FE 87 ?? ?? ?? ?? C7 87</c> (the signature GagSpeak and MeowyUtils hook, proven against
/// real keyboard input). Dalamud's <c>ScanText</c> already follows a leading <c>E8</c>/<c>E9</c> to the call target,
/// so the scan result is the function itself. Both are logged at Information; the choice is
/// <see cref="ChatInputHookTarget.Choose"/> (prefer the call-site target when they differ, with a warning). Exactly one
/// hook is installed.</para>
/// <para>The detour runs on the framework thread (the game's chat box handles Enter there).</para>
/// </remarks>
public sealed unsafe class ChatSendHook : IDisposable
{
    /// <summary>Call site in the chat box's Enter handling; starts with <c>E8</c>, so the scan yields the call's target.</summary>
    private const string ChatBoxCallSiteSignature = "E8 ?? ?? ?? ?? FE 87 ?? ?? ?? ?? C7 87";

    private delegate void ChatInputDelegate(ShellCommandModule* self, Utf8String* message, UIModule* uiModule);

    private readonly Configuration configuration;
    private readonly IChatSender sender;
    private readonly ILog log;
    private readonly Hook<ChatInputDelegate>? hook;
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
            var csAddress = ResolveClientStructsAddress();
            var sigAddress = ResolveChatBoxCallTarget();
            log.Information($"[JpEnChat] chat input hook: ExecuteCommandInner={csAddress:X}, chat-box call target={sigAddress:X}");

            var (address, source) = ChatInputHookTarget.Choose(csAddress, sigAddress);
            switch (source)
            {
                case ChatInputHookSource.None:
                    log.Warning("[JpEnChat] chat input function not found (neither ExecuteCommandInner nor the chat-box call target resolved); translating from the game's chat box is disabled.");
                    return;
                case ChatInputHookSource.CallSiteDiffers:
                    log.Warning($"[JpEnChat] ExecuteCommandInner ({csAddress:X}) differs from the chat-box call target ({sigAddress:X}); hooking the chat-box call target.");
                    break;
            }

            hook = Services.GameInteropProvider.HookFromAddress<ChatInputDelegate>(address, Detour);
            hook.Enable();
            log.Information($"[JpEnChat] chat input hook installed at {address:X} ({source}).");
        }
        catch (Exception ex)
        {
            hook?.Dispose();
            hook = null;
            log.Error(ex, "[JpEnChat] could not hook the chat input function; translating from the game's chat box is disabled.");
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
    /// <remarks>
    /// <see cref="GameChatSender"/> calls <c>UIModule.ProcessChatBoxEntry</c>, which calls the hooked chat input
    /// function synchronously on this same thread, so the detour runs inside <c>sender.Send</c>. <see cref="bypass"/>
    /// is set before that call and cleared in <c>finally</c>, so the re-entrant detour always passes the line straight
    /// to the original, and the flag can never stay set after a throw.
    /// </remarks>
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

    /// <summary>ClientStructs' address for <c>ShellCommandModule.ExecuteCommandInner</c>; 0 if unresolved.</summary>
    private nint ResolveClientStructsAddress()
    {
        try
        {
            return ShellCommandModule.Addresses.ExecuteCommandInner.Value;
        }
        catch (Exception ex)
        {
            log.Warning($"[JpEnChat] ExecuteCommandInner address unavailable ({ex.GetType().Name}).");
            return nint.Zero;
        }
    }

    /// <summary>
    /// The function the chat box calls at <see cref="ChatBoxCallSiteSignature"/>; 0 if not found. Dalamud's
    /// <c>ScanText</c> resolves a signature that starts with <c>E8</c> (or <c>E9</c>) to the call's destination
    /// (<c>SigScanner.ReadJmpCallSig</c>: <c>address + 5 + rel32</c>), so the result is not resolved again here.
    /// </summary>
    private nint ResolveChatBoxCallTarget()
    {
        try
        {
            return Services.SigScanner.TryScanText(ChatBoxCallSiteSignature, out var target) ? target : nint.Zero;
        }
        catch (Exception ex)
        {
            log.Warning($"[JpEnChat] chat-box call-site scan failed ({ex.GetType().Name}).");
            return nint.Zero;
        }
    }

    private void Detour(ShellCommandModule* self, Utf8String* message, UIModule* uiModule)
    {
        // Our own sends re-enter here from ProcessChatBoxEntry; bypass covers them (see SendBypassingHook).
        if (bypass || disposed || message == null || OnIntercept == null)
        {
            hook!.Original(self, message, uiModule);
            return;
        }

        string raw;
        InterceptResult decision;
        try
        {
            if (IsMacroRunning())
            {
                hook!.Original(self, message, uiModule);
                return;
            }

            raw = message->ToString();
            decision = InterceptDecision.Decide(raw, configuration, IsBypassModifierHeld());
        }
        catch (Exception ex)
        {
            LogOnce(ex);
            hook!.Original(self, message, uiModule);
            return;
        }

        switch (decision.Action)
        {
            case InterceptAction.PassRewritten:
                SendRewritten(self, message, uiModule, decision.Rewritten);
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
                    hook!.Original(self, message, uiModule);
                    return;
                }

                log.Debug($"[JpEnChat] held a chat-box line for translation ({ChatSendValidation.ChannelPrefix(raw)}).");
                return;

            default:
                hook!.Original(self, message, uiModule);
                return;
        }
    }

    /// <summary>Calls the original function with the bypass prefix removed; falls back to the unchanged line on error.</summary>
    private void SendRewritten(ShellCommandModule* self, Utf8String* message, UIModule* uiModule, string rewritten)
    {
        Utf8String* replacement;
        try
        {
            replacement = Utf8String.FromString(rewritten);
        }
        catch (Exception ex)
        {
            LogOnce(ex);
            hook!.Original(self, message, uiModule);
            return;
        }

        try
        {
            hook!.Original(self, replacement, uiModule);
        }
        finally
        {
            replacement->Dtor(true);
        }
    }

    /// <summary>
    /// A macro line goes through the same function; it is never held (a macro is not the user typing in the chat box).
    /// <c>MacroCurrentLine</c> is negative when no macro is running (as SimpleTweaks' command tweaks read it).
    /// </summary>
    private static bool IsMacroRunning()
    {
        var shell = RaptureShellModule.Instance();
        return shell != null && shell->MacroCurrentLine >= 0;
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
