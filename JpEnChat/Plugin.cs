using System;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using JpEnChat.Chat;
using JpEnChat.Models;
using JpEnChat.Translation;
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
    private readonly LruTranslationCache cache;
    private readonly OpenRouterClient client;
    private readonly TranslationPipeline pipeline;
    private readonly IChatSender chatSender;
    private readonly ChatIngest ingest;

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

        // Translation core (PLAN §3.2–3.3). The cache is loaded once here; the pipeline saves it periodically and on dispose.
        var log = new PluginLogAdapter(Services.Log);
        cache = new LruTranslationCache(pluginInterface.GetPluginConfigDirectory(), () => Configuration.MaxCacheEntries, log);
        cache.Load();
        client = new OpenRouterClient(() => Configuration.OpenRouterKey, () => Configuration.RequestTimeoutSeconds, log);
        var translator = new OpenRouterTranslator(Configuration, client, log);
        pipeline = new TranslationPipeline(
            Configuration,
            new ScriptLanguageDetector(),
            cache,
            translator,
            action => Services.Framework.RunOnFrameworkThread(action),
            log);

        // Game I/O (PLAN §3.1, §4.1 step 4).
        chatSender = new GameChatSender(log);

        configWindow = new ConfigWindow(Configuration, cache.Clear, () => cache.Count);
        mainWindow = new MainWindow(
            Configuration,
            ChatLog,
            new PipelineOutgoingTranslator(pipeline),
            SendChat,
            pipeline.Retry,
            CurrentWorldName,
            LocalPlayerName,
            OpenConfig);

        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);

        Services.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the JP/EN chat window. \"/jpchat config\" opens settings.",
        });

        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Subscribe last, so no chat line arrives before everything it touches exists.
        ingest = new ChatIngest(Configuration, ChatLog, pipeline, log);
    }

    public Configuration Configuration { get; }

    /// <summary>Rows shown in the main window. Append on the framework thread.</summary>
    public ChatLog ChatLog { get; }

    public void Dispose()
    {
        // Stop new lines first, then the UI, then cancel in-flight translations (the pipeline saves the cache).
        ingest.Dispose();

        pluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        pluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;

        Services.CommandManager.RemoveHandler(CommandName);

        windowSystem.RemoveAllWindows();
        configWindow.Dispose();
        mainWindow.Dispose();

        pipeline.Dispose();
        client.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var sub = args.Trim();
        if (sub.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            OpenConfig();
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

    /// <summary>
    /// Sends one confirmed chat command on the framework thread. Registers it with ingest first so the game's echo of a
    /// tell is not logged twice (the composer adds the sent row itself).
    /// </summary>
    private Task SendChat(string command) =>
        Services.Framework.RunOnFrameworkThread(() =>
        {
            ingest.ExpectOwnEcho(command);
            chatSender.Send(command);
        });
}
