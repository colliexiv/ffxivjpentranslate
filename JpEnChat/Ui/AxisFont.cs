using System;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;

namespace JpEnChat.Ui;

/// <summary>The game's Axis font at <see cref="Configuration.FontSizePx"/>, rebuilt when the setting changes.</summary>
internal sealed class AxisFont(Configuration configuration) : IDisposable
{
    private IFontHandle? handle;
    private float sizeBuilt;

    /// <summary>(Re)creates the handle if the configured size changed. Returns true when it was rebuilt.</summary>
    public bool Ensure()
    {
        var size = Math.Clamp(configuration.FontSizePx, 10f, 24f);
        if (handle != null && Math.Abs(size - sizeBuilt) < 0.01f)
        {
            return false;
        }

        handle?.Dispose();
        handle = Services.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, size));
        sizeBuilt = size;
        return true;
    }

    /// <summary>Pushes the font when it is available; dispose the result to pop. Null (no-op) while it is loading.</summary>
    public IDisposable? Push() => handle is { Available: true } h ? h.Push() : null;

    public void Dispose()
    {
        handle?.Dispose();
        handle = null;
    }
}
