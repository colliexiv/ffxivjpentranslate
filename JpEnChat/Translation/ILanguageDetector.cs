using JpEnChat.Models;

namespace JpEnChat.Translation;

/// <summary>
/// Offline script-based language detection (Phase 2A, PLAN §3.2): any Hiragana (U+3040–309F),
/// Katakana (U+30A0–30FF) or CJK ideograph (U+4E00–9FFF) → <see cref="Lang.Ja"/>; otherwise Latin letters →
/// <see cref="Lang.En"/>; otherwise <see cref="Lang.Other"/>/<see cref="Lang.Unknown"/>. No network, no models.
/// </summary>
/// <remarks>Must be pure and allocation-light; it runs on the framework thread for every ingested line.</remarks>
public interface ILanguageDetector
{
    Lang Detect(string text);
}
