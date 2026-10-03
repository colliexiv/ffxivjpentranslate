using System;
using Dalamud.Game.Text;
using JpEnChat.Models;
using JpEnChat.Translation;
using JpEnChat.Ui;

namespace JpEnChat.Chat;

/// <summary>What <see cref="ChatSendHook"/> does with one line the game's chat box is about to send.</summary>
public enum InterceptAction
{
    /// <summary>Send the line unchanged (call the original game function).</summary>
    Pass,

    /// <summary>Send <see cref="InterceptResult.Rewritten"/> instead (the bypass prefix was stripped).</summary>
    PassRewritten,

    /// <summary>Do not send; open the quick-translate popup for <see cref="InterceptResult.Body"/>.</summary>
    Intercept,
}

/// <summary>Result of <see cref="InterceptDecision.Decide(string, bool, string, bool)"/>.</summary>
/// <param name="Action">What to do.</param>
/// <param name="ChannelPrefix">For <see cref="InterceptAction.Intercept"/>: the channel command exactly as typed,
/// including its trailing whitespace (e.g. <c>"/t Tanaka Taro@Gaia "</c>), or empty when none was typed.</param>
/// <param name="Body">For <see cref="InterceptAction.Intercept"/>: the English to translate, trimmed.</param>
/// <param name="Rewritten">For <see cref="InterceptAction.PassRewritten"/>: the line to send instead.</param>
/// <param name="ChannelKind">For <see cref="InterceptAction.Intercept"/>: the typed command's channel, or <c>null</c>
/// when no prefix was typed (the chat box's selected channel applies).</param>
/// <param name="TellTarget">For a typed <c>/t</c>: the target as typed. Empty otherwise.</param>
public readonly record struct InterceptResult(
    InterceptAction Action,
    string ChannelPrefix = "",
    string Body = "",
    string Rewritten = "",
    XivChatType? ChannelKind = null,
    string TellTarget = "")
{
    public static readonly InterceptResult Pass = new(InterceptAction.Pass);
}

/// <summary>
/// Decides whether a line sent from the game's chat box is plain English chat that should be translated first
/// (PLAN §9). Pure; the hook supplies the text, settings and modifier state.
/// </summary>
/// <remarks>
/// Rules, in order. Anything that is not plainly English chat passes through untouched:
/// <list type="number">
/// <item>Interception disabled → Pass.</item>
/// <item>The bypass modifier (e.g. Ctrl, for Ctrl+Enter) is held → Pass.</item>
/// <item>Empty, or contains a control character (the raw string of an item link or auto-translate phrase) → Pass.</item>
/// <item>Starts with the bypass prefix (default <c>\</c>) → PassRewritten with the prefix removed. The prefix is also
/// recognized right after a channel command (<c>/p \hi</c> → <c>/p hi</c>). Nothing left after it → Pass.</item>
/// <item>Starts with <c>/</c> but not a chat-channel command (<see cref="OutgoingChannels.TrySplitChatCommand"/>;
/// <c>/e</c> echo counts as "not") → Pass. So emotes and plugin commands are never touched.</item>
/// <item>The body (after any channel command) is empty, only links/numbers/symbols, or contains Japanese → Pass.</item>
/// <item>Otherwise → Intercept.</item>
/// </list>
/// </remarks>
public static class InterceptDecision
{
    private static readonly ScriptLanguageDetector Detector = new();

    /// <summary>Uses <see cref="Configuration.InterceptVanillaChat"/> and <see cref="Configuration.BypassPrefix"/>.</summary>
    public static InterceptResult Decide(string text, Configuration configuration, bool modifierHeld)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Decide(text, configuration.InterceptVanillaChat, configuration.BypassPrefix, modifierHeld);
    }

    /// <param name="text">The line as the chat box would send it.</param>
    /// <param name="enabled">Interception is on.</param>
    /// <param name="bypassPrefix">Prefix that sends the rest untranslated; empty disables the prefix.</param>
    /// <param name="modifierHeld">The configured bypass modifier key is held.</param>
    public static InterceptResult Decide(string? text, bool enabled, string? bypassPrefix, bool modifierHeld)
    {
        if (!enabled || modifierHeld || string.IsNullOrWhiteSpace(text) || HasControlCharacter(text))
        {
            return InterceptResult.Pass;
        }

        var bypass = bypassPrefix?.Trim() ?? string.Empty;
        var trimmed = text.Trim();

        if (bypass.Length > 0 && trimmed.StartsWith(bypass, StringComparison.Ordinal))
        {
            var rest = trimmed[bypass.Length..].TrimStart();
            return rest.Length == 0 ? InterceptResult.Pass : new InterceptResult(InterceptAction.PassRewritten, Rewritten: rest);
        }

        var prefix = string.Empty;
        var body = trimmed;
        XivChatType? kind = null;
        var tellTarget = string.Empty;

        if (trimmed[0] == '/')
        {
            if (!OutgoingChannels.TrySplitChatCommand(trimmed, out var split))
            {
                return InterceptResult.Pass;
            }

            prefix = split.Prefix;
            body = split.Body;
            kind = split.Kind;
            tellTarget = split.TellTarget;

            if (bypass.Length > 0 && body.StartsWith(bypass, StringComparison.Ordinal))
            {
                var rest = body[bypass.Length..].TrimStart();
                return rest.Length == 0
                    ? InterceptResult.Pass
                    : new InterceptResult(InterceptAction.PassRewritten, Rewritten: prefix + rest);
            }
        }

        if (!IsPlainEnglish(body))
        {
            return InterceptResult.Pass;
        }

        return new InterceptResult(InterceptAction.Intercept, prefix, body, ChannelKind: kind, TellTarget: tellTarget);
    }

    /// <summary>
    /// True when <paramref name="body"/> is English per <see cref="ScriptLanguageDetector"/> once URL tokens are
    /// ignored. Japanese, empty, numbers/symbols/emoji only and URL-only bodies are not.
    /// </summary>
    public static bool IsPlainEnglish(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        if (Detector.Detect(body) != Lang.En)
        {
            return false;
        }

        // Latin letters present; make sure they are not all inside links.
        foreach (var token in body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsLink(token) && Detector.Detect(token) == Lang.En)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLink(string token) =>
        token.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

    private static bool HasControlCharacter(string text)
    {
        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }
}
