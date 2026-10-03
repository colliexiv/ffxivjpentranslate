using System;

namespace JpEnChat.Translation;

/// <summary>
/// Minimal logging surface for the translation core, so it can be unit-tested without Dalamud.
/// The plugin adapts <c>IPluginLog</c> to it via <see cref="PluginLogAdapter"/>.
/// </summary>
/// <remarks>Never pass API keys, and never pass full request bodies above <see cref="Debug"/> level.</remarks>
public interface ILog
{
    void Debug(string message);

    void Information(string message);

    void Warning(string message);

    void Error(Exception? exception, string message);
}

/// <summary>An <see cref="ILog"/> that discards everything.</summary>
public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Debug(string message)
    {
    }

    public void Information(string message)
    {
    }

    public void Warning(string message)
    {
    }

    public void Error(Exception? exception, string message)
    {
    }
}
