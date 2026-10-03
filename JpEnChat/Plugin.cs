using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
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

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;

        if (pluginInterface.Create<Services>() is null)
        {
            throw new InvalidOperationException("Dalamud service injection failed.");
        }

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Migrate();

        // TODO(Phase 2A): construct the translation pipeline here and pass it to consumers:
        //   ILanguageDetector  -> ScriptLanguageDetector (kana/kanji ranges, no network)
        //   ITranslationCache  -> LruTranslationCache(pluginInterface.GetPluginConfigDirectory(), Configuration); Load()
        //   ITranslator        -> OpenRouterTranslator(Configuration, Services.Log) with a SemaphoreSlim(MaxConcurrency)
        //   Gate               -> per-sender debounce + cache lookup, emits batches to ITranslator,
        //                         marshals deltas to ChatLine via Services.Framework.RunOnFrameworkThread
        // Dispose order on unload: cancel the pipeline's CancellationTokenSource, then cache.Save().

        // TODO(Phase 3): construct ingest and send:
        //   ChatIngest   -> subscribes Services.ChatGui.ChatMessage (IHandleableChatMessage), builds ChatLine,
        //                   pushes into the log store and the Gate. Unsubscribe in Dispose.
        //   IChatSender  -> GameChatSender (UIModule.ProcessChatBoxEntry, framework thread only).

        // TODO(Phase 2B): pass the log store, ITranslator and IChatSender into MainWindow instead of fake rows.
        mainWindow = new MainWindow(Configuration);
        configWindow = new ConfigWindow(Configuration);

        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);

        Services.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the JP/EN chat window. \"/jpchat config\" opens settings.",
        });

        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
    }

    public Configuration Configuration { get; }

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
        if (args.Trim().Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            configWindow.IsOpen = true;
            configWindow.BringToFront();
            return;
        }

        mainWindow.Toggle();
    }

    private void ToggleMainUi() => mainWindow.Toggle();

    private void ToggleConfigUi() => configWindow.Toggle();
}
