using System;
using Dalamud.Plugin.Services;

namespace JpEnChat.Translation;

/// <summary>Forwards <see cref="ILog"/> calls to Dalamud's <see cref="IPluginLog"/>.</summary>
public sealed class PluginLogAdapter(IPluginLog log) : ILog
{
    // Messages are pre-formatted; they go in as a template argument so braces in chat text or model output
    // are never parsed as Serilog message-template holes.
    public void Debug(string message) => log.Debug("{Message:l}", message);

    public void Information(string message) => log.Information("{Message:l}", message);

    public void Warning(string message) => log.Warning("{Message:l}", message);

    public void Error(Exception? exception, string message)
    {
        if (exception is null)
        {
            log.Error("{Message:l}", message);
        }
        else
        {
            log.Error(exception, "{Message:l}", message);
        }
    }
}
