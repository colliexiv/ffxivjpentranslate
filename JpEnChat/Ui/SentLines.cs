using System;
using JpEnChat.Models;

namespace JpEnChat.Ui;

/// <summary>Builds the log row for a line the plugin sent (left: English, right: Japanese).</summary>
internal static class SentLines
{
    /// <param name="kind">Channel of the row (colors, filter).</param>
    /// <param name="tellTarget">For tells: <c>Name Surname@World</c> (or as typed); the row shows the target as sender.
    /// <c>null</c> for other channels (the row shows the local player).</param>
    /// <param name="sent">What was sent.</param>
    /// <param name="localPlayerName">Local player name; "You" when empty.</param>
    public static ChatLine Build(Dalamud.Game.Text.XivChatType kind, string? tellTarget, SentMessage sent, string localPlayerName)
    {
        string name;
        var world = string.Empty;
        if (tellTarget != null)
        {
            var at = tellTarget.IndexOf('@');
            name = at < 0 ? tellTarget : tellTarget[..at];
            world = at < 0 ? string.Empty : tellTarget[(at + 1)..];
        }
        else
        {
            name = localPlayerName.Length == 0 ? "You" : localPlayerName;
        }

        return new ChatLine
        {
            Kind = kind,
            SenderName = name,
            SenderWorld = world,
            Original = sent.English,
            OriginalLang = Lang.En,
            Translation = sent.Japanese,
            Status = sent.Translated ? TranslationStatus.Done : TranslationStatus.None,
            IsOwn = true,
            IsSentByPlugin = true,
        };
    }
}

/// <summary>Marshals UI updates of the outgoing flow onto the framework thread.</summary>
internal static class FrameworkPost
{
    /// <summary>Queues <paramref name="action"/> on the framework thread; dropped (and logged at Debug) during unload.</summary>
    public static void Run(Action action)
    {
        try
        {
            _ = Services.Framework.RunOnFrameworkThread(action);
        }
        catch (Exception ex)
        {
            // Plugin unloading; nothing left to update.
            Services.Log.Debug($"Dropped outgoing UI update: {ex.GetType().Name}");
        }
    }
}
