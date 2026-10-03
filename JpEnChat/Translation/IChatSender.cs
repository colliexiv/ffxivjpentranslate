namespace JpEnChat.Translation;

/// <summary>
/// Sends a line through the game's chat box as if typed (Phase 3: <c>UIModule.ProcessChatBoxEntry</c>
/// after <c>Utf8String.SanitizeString</c>, PLAN §4.1).
/// </summary>
/// <remarks>
/// <para><b>Must be called on the framework thread</b> (wrap in <c>IFramework.RunOnFrameworkThread</c>).
/// Calling it from any other thread is undefined behaviour in the game client.</para>
/// <para>Only ever invoked in response to an explicit user confirmation (second Enter). Never send automatically.</para>
/// </remarks>
public interface IChatSender
{
    /// <summary>Maximum encoded length the game accepts for one chat line, in UTF-8 bytes (~166 Japanese characters).</summary>
    const int MaxMessageBytes = 500;

    /// <summary>Sends <paramref name="text"/>, including any channel prefix such as <c>"/p "</c>.</summary>
    /// <exception cref="System.ArgumentException">
    /// Empty, longer than <see cref="MaxMessageBytes"/> UTF-8 bytes, or changed by the game's sanitizer
    /// (contains characters the chat box would strip).
    /// </exception>
    /// <exception cref="System.InvalidOperationException">Called off the framework thread.</exception>
    void Send(string text);
}
