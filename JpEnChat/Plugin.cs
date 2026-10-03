using System;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using JpEnChat.Models;
using JpEnChat.Ui;
using JpEnChat.Windows;

namespace JpEnChat;

/// <summary>
/// Plugin entry point. Composes services and windows and wires them to Dalamud; holds no business logic.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/jpchat";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly WindowSystem windowSystem = new("JpEnChat");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;

    private uint cachedWorldId = uint.MaxValue;
    private string cachedWorldName = string.Empty;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;

        if (pluginInterface.Create<Services>() is null)
        {
            throw new InvalidOperationException("Dalamud service injection failed.");
        }

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Migrate();

        ChatLog = new ChatLog(() => Configuration.MaxLogLines);

        // TODO(Phase 2A): construct the translation pipeline here and pass it to consumers:
        //   ILanguageDetector  -> ScriptLanguageDetector (kana/kanji ranges, no network)
        //   ITranslationCache  -> LruTranslationCache(pluginInterface.GetPluginConfigDirectory(), Configuration); Load()
        //   ITranslator        -> OpenRouterTranslator(Configuration, Services.Log) with a SemaphoreSlim(MaxConcurrency)
        //   Gate               -> per-sender debounce + cache lookup, emits batches to ITranslator,
        //                         marshals deltas to ChatLine via Services.Framework.RunOnFrameworkThread
        // Dispose order on unload: cancel the pipeline's CancellationTokenSource, then cache.Save().
        //
        // Phase 2B placeholders to replace once the pipeline exists:
        //   outgoingTranslator -> new TranslatorOutgoingAdapter(translator)
        //   RetryLine          -> re-submit the line to the Gate (set Status = Pending, Error = null first)
        //   cache callbacks    -> cache.Clear() / cache.Count for the settings window
        IOutgoingTranslator outgoingTranslator = new UnwiredOutgoingTranslator();

        // TODO(Phase 3): construct ingest and send:
        //   ChatIngest   -> subscribes Services.ChatGui.ChatMessage (IHandleableChatMessage), builds ChatLine,
        //                   pushes into ChatLog and the Gate. Unsubscribe in Dispose.
        //   IChatSender  -> GameChatSender (UIModule.ProcessChatBoxEntry, framework thread only); replace SendStub with
        //                   text => Services.Framework.RunOnFrameworkThread(() => chatSender.Send(text)).

        configWindow = new ConfigWindow(Configuration, ClearCacheStub, () => 0);
        mainWindow = new MainWindow(
            Configuration,
            ChatLog,
            outgoingTranslator,
            SendStub,
            RetryLine,
            CurrentWorldName,
            LocalPlayerName,
            OpenConfig);

        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);

        Services.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the JP/EN chat window. \"/jpchat config\" opens settings. "
                          + "\"/jpchat test\" adds sample rows (development aid).",
        });

        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
    }

    public Configuration Configuration { get; }

    /// <summary>Rows shown in the main window. Append on the framework thread.</summary>
    public ChatLog ChatLog { get; }

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        pluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;

        Services.CommandManager.RemoveHandler(CommandName);

        // TODO(Phase 3): unsubscribe ChatIngest before tearing down the pipeline.
        // TODO(Phase 2A): cancel in-flight translations, then persist ITranslationCache.

        windowSystem.RemoveAllWindows();
        configWindow.Dispose();
        mainWindow.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var sub = args.Trim();
        if (sub.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            OpenConfig();
            return;
        }

        if (sub.Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            AddSampleRows();
            mainWindow.IsOpen = true;
            return;
        }

        mainWindow.Toggle();
    }

    private void ToggleMainUi() => mainWindow.Toggle();

    private void ToggleConfigUi() => configWindow.Toggle();

    private void OpenConfig()
    {
        configWindow.IsOpen = true;
        configWindow.BringToFront();
    }

    /// <summary>Current world of the local player, cached by row id so the draw loop does not allocate.</summary>
    private string CurrentWorldName()
    {
        if (!Services.PlayerState.IsLoaded)
        {
            return string.Empty;
        }

        var world = Services.PlayerState.CurrentWorld;
        if (world.RowId != cachedWorldId)
        {
            cachedWorldId = world.RowId;
            cachedWorldName = world.ValueNullable?.Name.ExtractText() ?? string.Empty;
        }

        return cachedWorldName;
    }

    private static string LocalPlayerName() =>
        Services.PlayerState.IsLoaded ? Services.PlayerState.CharacterName : string.Empty;

    // ---- Phase 2B placeholders (see TODOs in the constructor) ----

    private static Task SendStub(string text)
    {
        Services.Log.Info($"[JpEnChat] send not wired (Phase 3); would send {text.Length} chars.");
        return Task.FromException(new InvalidOperationException("Sending is not wired yet (Phase 3)."));
    }

    private static void RetryLine(ChatLine line)
    {
        Services.Log.Info($"[JpEnChat] retry requested for line {line.Id}; pipeline not wired (Phase 2A).");
    }

    private static void ClearCacheStub()
    {
        Services.Log.Info("[JpEnChat] clear cache requested; cache not wired (Phase 2A).");
    }

    /// <summary>
    /// Development aid for checking the UI in-game before ingest exists: adds a translated, a pending and a failed row.
    /// TODO(release): remove together with the "test" subcommand once ingest (Phase 3) lands.
    /// </summary>
    private void AddSampleRows()
    {
        var world = CurrentWorldName();
        ChatLog.Add(new ChatLine
        {
            Kind = XivChatType.Party,
            SenderName = "Tanaka Taro",
            SenderWorld = world,
            Original = "よろしくお願いします！",
            OriginalLang = Lang.Ja,
            Status = TranslationStatus.Done,
            Translation = "Nice to meet you, looking forward to working with you!",
        });
        ChatLog.Add(new ChatLine
        {
            Kind = XivChatType.FreeCompany,
            SenderName = "Suzuki Hanako",
            SenderWorld = "Tonberry",
            Original = "1ボス行きます、マーカーの位置に散開してください",
            OriginalLang = Lang.Ja,
            Status = TranslationStatus.Pending,
        });
        ChatLog.Add(new ChatLine
        {
            Kind = XivChatType.Ls1,
            SenderName = "Sato Jiro",
            SenderWorld = "Ramuh",
            Original = "今日のレイドは21時からです。遅れる人は連絡ください。",
            OriginalLang = Lang.Ja,
            Status = TranslationStatus.Failed,
            Error = "HTTP 429: rate limited (sample row)",
        });
    }
}
