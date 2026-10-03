using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using JpEnChat.Models;
using JpEnChat.Translation;

namespace JpEnChat.Chat;

/// <summary>
/// Captures chat from <c>IChatGui.ChatMessage</c> into <see cref="ChatLog"/> and the translation pipeline (PLAN §3.1).
/// </summary>
/// <remarks>
/// <para>The handler runs on the framework thread and only copies what it needs: a row appears in the window the same
/// frame, with "…" in the translation cell until the pipeline fills it. It never marks a message handled and never
/// modifies it, and it never lets an exception escape into Dalamud's event dispatch.</para>
/// <para><b>Own messages.</b> Lines from the local player are skipped (the outgoing composer adds its own rows), except
/// <see cref="XivChatType.TellOutgoing"/>, whose sender is the tell target: it is kept as an own row so tells typed in
/// the vanilla chat box show up. A tell sent through this plugin already has a row, so its echo is dropped via
/// <see cref="ExpectOwnEcho"/>.</para>
/// </remarks>
public sealed class ChatIngest : IDisposable
{
    private readonly Configuration configuration;
    private readonly ChatLog chatLog;
    private readonly TranslationPipeline pipeline;
    private readonly ILog log;
    private readonly RecentSends recentSends = new();
    private readonly HashSet<Type> loggedErrors = [];
    private bool disposed;

    public ChatIngest(Configuration configuration, ChatLog chatLog, TranslationPipeline pipeline, ILog log)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(chatLog);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(log);
        this.configuration = configuration;
        this.chatLog = chatLog;
        this.pipeline = pipeline;
        this.log = log;

        Services.ChatGui.ChatMessage += OnChatMessage;
    }

    /// <summary>
    /// Records a chat command the plugin is about to send, so the game's <see cref="XivChatType.TellOutgoing"/> echo of
    /// it is not added as a second row. Framework thread only.
    /// </summary>
    public void ExpectOwnEcho(string command) => recentSends.Add(command, DateTime.UtcNow);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Services.ChatGui.ChatMessage -= OnChatMessage;
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            Handle(message);
        }
        catch (Exception ex)
        {
            // Once per exception type, so a systematic failure cannot flood the log on every chat line.
            if (loggedErrors.Add(ex.GetType()))
            {
                log.Error(ex, $"[JpEnChat] chat ingest failed ({ex.GetType().Name}); further errors of this type are not logged.");
            }
        }
    }

    private void Handle(IHandleableChatMessage message)
    {
        if (disposed || message.IsHandled)
        {
            return;
        }

        var kind = message.LogKind;
        if (!configuration.EnabledChannels.Contains(kind))
        {
            return;
        }

        var player = message.Sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        string senderName;
        string senderWorld;
        uint? senderWorldId = null;
        if (player is not null)
        {
            senderName = SeStringText.CleanName(player.PlayerName ?? string.Empty);
            senderWorldId = player.World.RowId;
            senderWorld = player.World.ValueNullable?.Name.ExtractText() ?? string.Empty;
        }
        else
        {
            senderName = SeStringText.CleanName(message.Sender.TextValue);
            senderWorld = LocalCurrentWorldName();
        }

        var isTellOutgoing = kind == XivChatType.TellOutgoing;
        if (!isTellOutgoing && IsLocalPlayer(senderName, senderWorldId))
        {
            return;
        }

        var text = SeStringText.Flatten(message.Message);
        if (text.Length == 0)
        {
            return;
        }

        if (isTellOutgoing && recentSends.TryConsumeEcho(text, DateTime.UtcNow))
        {
            return;
        }

        var line = new ChatLine
        {
            Kind = kind,
            SenderName = senderName,
            SenderWorld = senderWorld,
            Original = text,
            OriginalLang = Lang.Unknown,
            IsOwn = isTellOutgoing,
            Timestamp = DateTime.Now,
        };

        chatLog.Add(line);
        pipeline.Enqueue(line);
    }

    /// <summary>
    /// The local player's own lines carry no player link, so a name match without a world is own. With a link, the
    /// world must also be the local home world (another player can have the same name on a different world).
    /// </summary>
    private static bool IsLocalPlayer(string senderName, uint? senderWorldId)
    {
        var playerState = Services.PlayerState;
        if (!playerState.IsLoaded || senderName.Length == 0)
        {
            return false;
        }

        if (!string.Equals(senderName, playerState.CharacterName, StringComparison.Ordinal))
        {
            return false;
        }

        return senderWorldId is null || senderWorldId.Value == playerState.HomeWorld.RowId;
    }

    private static string LocalCurrentWorldName()
    {
        var playerState = Services.PlayerState;
        return playerState.IsLoaded ? playerState.CurrentWorld.ValueNullable?.Name.ExtractText() ?? string.Empty : string.Empty;
    }
}

/// <summary>
/// Chat commands the plugin sent recently, used to recognize the game's echo of a tell the plugin already logged.
/// Not thread-safe; used on the framework thread only.
/// </summary>
public sealed class RecentSends
{
    /// <summary>An echo arriving later than this after the send is treated as a new message.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    /// <summary>At most this many sends are remembered; older ones are forgotten first.</summary>
    public const int Capacity = 8;

    private readonly List<(string Command, DateTime At)> entries = [];

    public int Count => entries.Count;

    public void Add(string command, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(command);
        Prune(nowUtc);
        entries.Add((command, nowUtc));
        if (entries.Count > Capacity)
        {
            entries.RemoveAt(0);
        }
    }

    /// <summary>
    /// True (and forgets the entry) when a remembered command ends with <paramref name="echoText"/>, i.e. the echo is
    /// the body of a command such as <c>/t Name@World body</c>.
    /// </summary>
    public bool TryConsumeEcho(string echoText, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(echoText);
        Prune(nowUtc);
        if (echoText.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var command = SeStringText.CollapseWhitespace(entries[i].Command);
            if (command.EndsWith(" " + echoText, StringComparison.Ordinal))
            {
                entries.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    private void Prune(DateTime nowUtc) => entries.RemoveAll(e => nowUtc - e.At > Window);
}
