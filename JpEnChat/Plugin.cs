using System;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using JpEnChat.Chat;
using JpEnChat.Models;
using JpEnChat.PartyFinder;
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
    private readonly AnthropicClient anthropicClient;
    private readonly GameContextProvider gameContext = new();
    private readonly TranslationPipeline pipeline;
    private readonly ChatSendHook chatSendHook;
    private readonly QuickTranslatePopup quickPopup;
    private readonly ChatBarButton chatBarButton;
    private readonly ChatIngest ingest;
    private readonly PartyFinderPopup partyFinderPopup;
    private readonly PartyFinderTranslator partyFinder;

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
        var firstRun = !File.Exists(cache.FilePath);
        cache.Load();
        if (firstRun)
        {
            // Starter fixed translations (PLAN §11), only when there is no cache file yet; later via "Add defaults".
            FixedTranslationDefaults.AddTo(cache);
            cache.Save();
        }

        // Providers (PLAN §12): both clients live for the whole session; BackendSwitch picks one per request, so changing
        // the provider in the settings applies to the next request.
        client = new OpenRouterClient(() => Configuration.OpenRouterKey, () => Configuration.RequestTimeoutSeconds, log);
        anthropicClient = new AnthropicClient(
            () => Configuration.AnthropicKey,
            () => Configuration.RequestTimeoutSeconds,
            () => Configuration.AnthropicRefusalFallback,
            log);
        var backend = new BackendSwitch(() => Configuration.Provider, client, anthropicClient);
        var translator = new LlmTranslator(Configuration, backend, log);
        var detector = new ScriptLanguageDetector();
        var contextBuilder = new TranslationContextBuilder(
            ChatLog, () => Configuration.ContextLines, gameContext.Current, ChatChannels.DisplayName);
        pipeline = new TranslationPipeline(
            Configuration,
            detector,
            cache,
            translator,
            action => Services.Framework.RunOnFrameworkThread(action),
            log,
            contextBuilder.ForIncoming);

        // Game I/O (PLAN §3.1, §4.1 step 4). Every plugin send goes through the hook's bypass (PLAN §9).
        var chatSender = new GameChatSender(log);
        chatSendHook = new ChatSendHook(Configuration, chatSender, log);

        var outgoingTranslator = new PipelineOutgoingTranslator(pipeline);
        configWindow = new ConfigWindow(Configuration, cache, pipeline.RequestCacheSave, () => chatSendHook.IsInstalled);
        mainWindow = new MainWindow(Configuration, ChatLog, pipeline, CurrentWorldName, OpenConfig);

        // Vanilla chat integration (PLAN §9): popup for lines typed into the game's chat box, and the chat-bar button.
        quickPopup = new QuickTranslatePopup(
            Configuration, ChatLog, outgoingTranslator, SendChat, LocalPlayerName, contextBuilder.ForOutgoing);
        chatBarButton = new ChatBarButton(Configuration, ToggleMainUi);

        // Party Finder (PLAN §10): popup next to a listing's detail window.
        partyFinderPopup = new PartyFinderPopup(Configuration, pipeline.Retry);

        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);

        Services.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the JP/EN chat window. \"/jpchat config\" opens settings. "
                + "\"/jpchat auto\" toggles translating English typed into the game's chat box.",
        });

        pluginInterface.UiBuilder.Draw += OnDraw;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Subscribe last, so no chat line arrives before everything it touches exists.
        ingest = new ChatIngest(Configuration, ChatLog, pipeline, log);
        partyFinder = new PartyFinderTranslator(Configuration, ChatLog, pipeline, detector, partyFinderPopup, ShowMainUi, log);
        chatSendHook.OnIntercept = quickPopup.TryBegin;
    }

    public Configuration Configuration { get; }

    /// <summary>Rows shown in the main window. Append on the framework thread.</summary>
    public ChatLog ChatLog { get; }

    public void Dispose()
    {
        // Stop new lines first (ingest, the Party Finder context menu, then the chat-box hook so nothing more is
        // intercepted), then the popups and the UI, then cancel in-flight translations (the pipeline saves the cache).
        ingest.Dispose();
        partyFinder.Dispose();
        chatSendHook.Dispose();
        quickPopup.Dispose();
        partyFinderPopup.Dispose();

        pluginInterface.UiBuilder.Draw -= OnDraw;
        pluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        pluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;

        Services.CommandManager.RemoveHandler(CommandName);

        windowSystem.RemoveAllWindows();
        configWindow.Dispose();
        mainWindow.Dispose();

        pipeline.Dispose();
        client.Dispose();
        anthropicClient.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var sub = args.Trim();
        if (sub.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            OpenConfig();
            return;
        }

        if (sub.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            Configuration.InterceptVanillaChat = !Configuration.InterceptVanillaChat;
            Configuration.Save();
            var state = Configuration.InterceptVanillaChat ? "on" : "off";
            var note = chatSendHook.IsInstalled ? string.Empty : " (unavailable: the chat-box hook could not be installed; see /xllog)";
            Services.ChatGui.Print($"[JP/EN Chat] Translating English typed into the chat box: {state}{note}.");
            return;
        }

        mainWindow.Toggle();
    }

    private void OnDraw()
    {
        windowSystem.Draw();
        chatBarButton.Draw();
        quickPopup.Draw();
        partyFinderPopup.Draw();
    }

    private void ToggleMainUi() => mainWindow.Toggle();

    private void ShowMainUi()
    {
        mainWindow.IsOpen = true;
        mainWindow.BringToFront();
    }

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
    /// Sends one confirmed chat command on the framework thread, bypassing the chat-box hook so the plugin's own line is
    /// never intercepted. Registers it with ingest first so the game's echo of a tell is not logged twice (the
    /// popup adds the sent row itself).
    /// </summary>
    private Task SendChat(string command) =>
        Services.Framework.RunOnFrameworkThread(() =>
        {
            ingest.ExpectOwnEcho(command);
            chatSendHook.SendBypassingHook(command);
        });
}
