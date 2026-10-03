using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace JpEnChat;

/// <summary>
/// Dalamud services, populated once by <see cref="Plugin"/> via
/// <c>IDalamudPluginInterface.Create&lt;Services&gt;()</c>.
/// </summary>
/// <remarks>
/// All members are static. The type itself is not declared <c>static</c> because Dalamud's
/// injector needs to construct an instance to run property injection; the instance is discarded.
/// Services are only valid between plugin construction and <see cref="Plugin.Dispose"/>.
/// </remarks>
internal sealed class Services
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] public static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] public static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] public static IKeyState KeyState { get; private set; } = null!;
}
