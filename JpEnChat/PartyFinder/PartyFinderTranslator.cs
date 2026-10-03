using System;
using System.Linq;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using JpEnChat.Models;
using JpEnChat.Translation;

namespace JpEnChat.PartyFinder;

/// <summary>
/// Adds "Translate" to the right-click menu of the game's Party Finder listing detail window and translates the
/// listing's description into the log and the <see cref="PartyFinderPopup"/> (PLAN §10).
/// </summary>
/// <remarks>
/// Context-menu events and clicks arrive on the framework thread, where log rows may be added and the pipeline fed.
/// The description becomes an ordinary <see cref="ChatLine"/> (<see cref="ChatLine.SourceLabel"/> "PF", leader as the
/// sender, duty as <see cref="ChatLine.Context"/>), so the cache, streaming and retry work unchanged. It is sent with
/// <see cref="TranslationPipeline.EnqueueImmediate"/>: a click is an explicit request, so it skips the debounce.
/// </remarks>
internal sealed class PartyFinderTranslator : IDisposable
{
    private readonly Configuration configuration;
    private readonly ChatLog log;
    private readonly TranslationPipeline pipeline;
    private readonly ILanguageDetector detector;
    private readonly PartyFinderPopup popup;
    private readonly Action showMainWindow;
    private readonly ILog logger;
    private readonly MenuItem menuItem;

    private ChatLine? lastLine;

    /// <param name="configuration">Settings (menu item on/off, popup on/off).</param>
    /// <param name="log">Row store the translation is added to.</param>
    /// <param name="pipeline">Translation pipeline.</param>
    /// <param name="detector">Language detection for the row's <see cref="ChatLine.OriginalLang"/>.</param>
    /// <param name="popup">Popup next to the listing.</param>
    /// <param name="showMainWindow">Opens the main window and brings it to the front (used when the popup is off).</param>
    /// <param name="logger">Plugin log.</param>
    public PartyFinderTranslator(
        Configuration configuration,
        ChatLog log,
        TranslationPipeline pipeline,
        ILanguageDetector detector,
        PartyFinderPopup popup,
        Action showMainWindow,
        ILog logger)
    {
        this.configuration = configuration;
        this.log = log;
        this.pipeline = pipeline;
        this.detector = detector;
        this.popup = popup;
        this.showMainWindow = showMainWindow;
        this.logger = logger;

        menuItem = new MenuItem
        {
            UseDefaultPrefix = true,
            Name = "Translate",
            OnClicked = OnClicked,
        };

        Services.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose()
    {
        Services.ContextMenu.OnMenuOpened -= OnMenuOpened;
        lastLine = null;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (configuration.PartyFinderContextMenu
            && string.Equals(args.AddonName, PartyFinderAddon.Name, StringComparison.Ordinal))
        {
            args.AddMenuItem(menuItem);
        }
    }

    private void OnClicked(IMenuItemClickedArgs args)
    {
        try
        {
            if (!string.Equals(args.AddonName, PartyFinderAddon.Name, StringComparison.Ordinal))
            {
                return;
            }

            if (PartyFinderAddon.Read(args.AddonPtr) is { } listing)
            {
                Translate(listing);
            }
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Translating a Party Finder listing failed.");
        }
    }

    private void Translate(PartyFinderListing listing)
    {
        var line = listing.Description.Length > 0 ? LineFor(listing) : null;

        if (configuration.PartyFinderPopup)
        {
            popup.Show(listing, line);
        }
        else if (line is null)
        {
            Services.ChatGui.Print("[JP/EN Chat] This Party Finder listing has no description.");
        }
        else
        {
            showMainWindow();
        }
    }

    /// <summary>
    /// The row for <paramref name="listing"/>: the previous one when the same listing is translated again and its row
    /// is still in the log (a failed one is retried), else a new row, added to the log and sent for translation.
    /// </summary>
    private ChatLine LineFor(PartyFinderListing listing)
    {
        if (lastLine is { } previous
            && string.Equals(previous.Original, listing.Description, StringComparison.Ordinal)
            && string.Equals(previous.SenderName, listing.Leader, StringComparison.Ordinal)
            && string.Equals(previous.Context ?? string.Empty, listing.Duty, StringComparison.Ordinal)
            && log.Snapshot().Contains(previous))
        {
            pipeline.Retry(previous); // no-op unless it failed
            return previous;
        }

        var line = new ChatLine
        {
            Kind = XivChatType.None,
            SourceLabel = ChatLine.PartyFinderSource,
            Context = listing.Duty.Length > 0 ? listing.Duty : null,
            SenderName = listing.Leader,
            Original = listing.Description,
            OriginalLang = detector.Detect(listing.Description),
        };

        log.Add(line);
        pipeline.EnqueueImmediate(line);
        lastLine = line;
        logger.Debug($"Party Finder listing queued for translation ({listing.Description.Length} chars, {line.Status}).");
        return line;
    }
}
