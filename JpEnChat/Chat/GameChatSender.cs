using System;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using JpEnChat.Translation;

namespace JpEnChat.Chat;

/// <summary>
/// Sends a line through the game's chat box, exactly as if the user typed it and pressed Enter (PLAN §4.1 step 4).
/// </summary>
/// <remarks>
/// <para>Uses <c>UIModule.ProcessChatBoxEntry</c>, the same entry point as ChatTwo's normal send path and ECommons'
/// <c>Chat.SendMessage</c>. It handles both chat commands (<c>/p …</c>, <c>/t Name@World …</c>) and plain text the way
/// the chat box does: <c>ProcessChatBoxEntry</c> hands the line on to <c>ShellCommandModule.ExecuteCommandInner</c>, the
/// shell's chat-input processor that handles plain chat text and commands alike (the game's chat box calls that function
/// directly on Enter). Because <see cref="ChatSendHook"/> hooks that function, every send here re-enters its detour on
/// this thread; <see cref="ChatSendHook.SendBypassingHook"/> sets its bypass flag around this call.</para>
/// <para>Only called after an explicit user confirmation (second Enter in the composer). Never sends automatically.</para>
/// </remarks>
public sealed unsafe class GameChatSender : IChatSender
{
    /// <summary>
    /// Flags passed to <c>Utf8String.SanitizeString</c>: upper/lower letters, numbers, special characters, character
    /// list, other characters (which covers kana and kanji), payloads, and bit 9. The same value ChatTwo and ECommons
    /// use, matching what the game's chat box accepts.
    /// </summary>
    private const AllowedEntities ChatBoxEntities = (AllowedEntities)0x27F;

    private readonly ILog log;

    public GameChatSender(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        this.log = log;
    }

    /// <inheritdoc />
    public void Send(string text)
    {
        if (!Services.Framework.IsInFrameworkUpdateThread)
        {
            throw new InvalidOperationException("Chat can only be sent from the framework thread.");
        }

        var bytes = ChatSendValidation.Validate(text);

        var message = Utf8String.FromString(text);
        try
        {
            message->SanitizeString(ChatBoxEntities);
            if (!string.Equals(message->ToString(), text, StringComparison.Ordinal))
            {
                throw new ArgumentException("Message contains characters the game's chat box does not allow.", nameof(text));
            }

            UIModule.Instance()->ProcessChatBoxEntry(message);
        }
        finally
        {
            message->Dtor(true);
        }

        log.Information($"[JpEnChat] sent chat line: {bytes} bytes, channel {ChatSendValidation.ChannelPrefix(text)}.");
    }
}
