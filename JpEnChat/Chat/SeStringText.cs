using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace JpEnChat.Chat;

/// <summary>Kind of a visible fragment extracted from an <see cref="SeString"/> payload.</summary>
public enum TextPieceKind
{
    /// <summary>Plain text (<see cref="TextPayload"/>), including the visible name of item/map/quest/status links.</summary>
    Text,

    /// <summary>A player link (<see cref="PlayerPayload"/>); <see cref="TextPiece.Text"/> is the player name.</summary>
    Player,

    /// <summary>An auto-translate phrase; <see cref="TextPiece.Text"/> is the resolved phrase, or empty if it failed.</summary>
    AutoTranslate,

    /// <summary>A line break (<see cref="NewLinePayload"/>).</summary>
    NewLine,
}

/// <summary>One visible fragment of a chat message, in payload order.</summary>
public readonly record struct TextPiece(TextPieceKind Kind, string Text);

/// <summary>
/// Turns a chat <see cref="SeString"/> into the plain text shown in the log and sent to the translator (PLAN §3.1).
/// </summary>
/// <remarks>
/// <para>Rules:</para>
/// <list type="bullet">
/// <item><see cref="TextPayload"/> text is kept. Item, map, quest, status and party-finder links carry their visible
/// name as a following <see cref="TextPayload"/>, so the link payloads themselves are ignored and the name is not
/// duplicated.</item>
/// <item><see cref="AutoTranslatePayload"/> becomes <c>《phrase》</c>. If the phrase cannot be resolved (no game data,
/// unknown key) it becomes <see cref="AutoTranslatePlaceholder"/>.</item>
/// <item><see cref="PlayerPayload"/> becomes the player name. The game follows a player link with a
/// <see cref="TextPayload"/> holding the same name; that text is skipped so the name appears once.</item>
/// <item><see cref="NewLinePayload"/> becomes a space. Icons, colors, glow, italics and raw payloads (including link
/// terminators) are dropped.</item>
/// <item>Characters in the Unicode private-use area (U+E000–U+F8FF: the game's icon glyphs, such as the item-link
/// arrow, party slot numbers and the auto-translate brackets) are replaced with a space. Finally, whitespace runs are
/// collapsed to one ASCII space and the result is trimmed.</item>
/// </list>
/// <para>The split into <see cref="ToPieces"/> and <see cref="Combine"/> exists for testing: several Dalamud payload
/// constructors and the auto-translate lookup need live game data, so the combining rules are tested on pieces.</para>
/// </remarks>
public static class SeStringText
{
    /// <summary>Shown for an auto-translate phrase that could not be resolved.</summary>
    public const string AutoTranslatePlaceholder = "《auto-translate》";

    /// <summary>Flattens <paramref name="message"/> to plain text. See the type remarks for the rules.</summary>
    /// <param name="message">Chat message or sender.</param>
    /// <param name="resolveAutoTranslate">
    /// Resolves an auto-translate phrase; defaults to <see cref="AutoTranslatePayload.Text"/>, which reads the game's
    /// Completion sheet. Exceptions are caught and produce <see cref="AutoTranslatePlaceholder"/>.
    /// </param>
    public static string Flatten(SeString message, Func<AutoTranslatePayload, string>? resolveAutoTranslate = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Combine(ToPieces(message.Payloads, resolveAutoTranslate ?? (p => p.Text)));
    }

    /// <summary>Maps payloads to visible pieces; payloads with no visible text produce nothing.</summary>
    public static IEnumerable<TextPiece> ToPieces(
        IEnumerable<Payload> payloads,
        Func<AutoTranslatePayload, string> resolveAutoTranslate)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(resolveAutoTranslate);

        foreach (var payload in payloads)
        {
            switch (payload)
            {
                case TextPayload text:
                    yield return new TextPiece(TextPieceKind.Text, text.Text ?? string.Empty);
                    break;
                case AutoTranslatePayload auto:
                    yield return new TextPiece(TextPieceKind.AutoTranslate, ResolveSafely(auto, resolveAutoTranslate));
                    break;
                case PlayerPayload player:
                    yield return new TextPiece(TextPieceKind.Player, player.PlayerName ?? string.Empty);
                    break;
                case NewLinePayload:
                    yield return new TextPiece(TextPieceKind.NewLine, string.Empty);
                    break;

                // ItemPayload, MapLinkPayload, QuestPayload, StatusPayload, PartyFinderPayload, DalamudLinkPayload:
                // their visible text follows as a TextPayload. IconPayload, UIForegroundPayload, UIGlowPayload,
                // EmphasisItalicPayload, SeHyphenPayload and RawPayload (link terminators etc.): not part of the text.
                default:
                    break;
            }
        }
    }

    /// <summary>Joins pieces into the final text (player-name de-duplication, glyph stripping, whitespace collapse).</summary>
    public static string Combine(IEnumerable<TextPiece> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);

        var sb = new StringBuilder();
        string? pendingPlayerName = null;
        foreach (var piece in pieces)
        {
            switch (piece.Kind)
            {
                case TextPieceKind.Text:
                {
                    var text = StripPrivateUse(piece.Text);
                    var isLinkName = pendingPlayerName is not null && text.Trim() == pendingPlayerName;
                    pendingPlayerName = null;
                    if (!isLinkName)
                    {
                        sb.Append(text);
                    }

                    break;
                }

                case TextPieceKind.Player:
                {
                    var name = StripPrivateUse(piece.Text).Trim();
                    sb.Append(name);
                    pendingPlayerName = name.Length > 0 ? name : null;
                    break;
                }

                case TextPieceKind.AutoTranslate:
                {
                    var phrase = CollapseWhitespace(StripPrivateUse(piece.Text));
                    sb.Append(phrase.Length > 0 ? "《" + phrase + "》" : AutoTranslatePlaceholder);
                    pendingPlayerName = null;
                    break;
                }

                case TextPieceKind.NewLine:
                    sb.Append(' ');
                    pendingPlayerName = null;
                    break;
            }
        }

        return CollapseWhitespace(sb.ToString());
    }

    /// <summary>True for the Unicode private-use area U+E000–U+F8FF, where the game puts its icon glyphs.</summary>
    public static bool IsPrivateUse(char c) => c is >= '' and <= '';

    /// <summary>Replaces every private-use character with a space (call <see cref="CollapseWhitespace"/> afterwards).</summary>
    public static string StripPrivateUse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.AsSpan().IndexOfAnyInRange('', '') < 0)
        {
            return text;
        }

        return string.Create(text.Length, text, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                span[i] = IsPrivateUse(src[i]) ? ' ' : src[i];
            }
        });
    }

    /// <summary>Trims and collapses every whitespace run to one ASCII space.</summary>
    public static string CollapseWhitespace(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Plain sender text: private-use glyphs (party slot, friend-list icons) removed, whitespace collapsed.</summary>
    public static string CleanName(string text) => CollapseWhitespace(StripPrivateUse(text ?? string.Empty));

    private static string ResolveSafely(AutoTranslatePayload payload, Func<AutoTranslatePayload, string> resolve)
    {
        try
        {
            return resolve(payload) ?? string.Empty;
        }
        catch (Exception)
        {
            // Unknown group/key or game data unavailable: the caller shows the placeholder.
            return string.Empty;
        }
    }
}
