using JpEnChat.Models;

namespace JpEnChat.Translation;

/// <summary>
/// What the log's right-click menu needs from the translation pipeline (PLAN §11): retry a failed row, correct a
/// translation by hand, and pin or unpin a row's translation as its fixed translation.
/// </summary>
/// <remarks>
/// Framework thread only. ImGui draw callbacks qualify: Dalamud raises <c>UiBuilder.Draw</c> on the framework
/// thread, which is what makes the <see cref="ChatLine"/> mutations here legal (see the threading contract there).
/// </remarks>
public interface ITranslationCorrections
{
    /// <summary>Re-sends a <see cref="TranslationStatus.Failed"/> line; no-op otherwise.</summary>
    void Retry(ChatLine line);

    /// <summary>
    /// Replaces the line's translation with <paramref name="translation"/> (status
    /// <see cref="TranslationStatus.Corrected"/>) and pins it in the cache for the line's direction.
    /// </summary>
    void Correct(ChatLine line, string translation);

    /// <summary>Pins the line's finished translation. Returns false when it has none.</summary>
    bool Pin(ChatLine line);

    /// <summary>Unpins the line's fixed translation; it stays cached as an ordinary entry.</summary>
    void Unpin(ChatLine line);

    /// <summary>Whether the line's source text has a fixed translation.</summary>
    bool IsPinned(ChatLine line);
}
