using VoxelEngine.Networking.Hosting;
using VoxelEngine.UI;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.Menus;

namespace VoxelEngine.App;

/// <summary>
/// The world this game is hosting, if any (<see cref="HostedServer"/>): started from the menu's HOST page, ticked
/// by Program once a frame before the client world, announced in the chat once the host is in and shown on the
/// pause menu (<see cref="PauseMenuView.HostLines"/>), and stopped by every way back to the menu and by the window
/// closing. The port and UPnP choice of a host that started are remembered
/// (<see cref="GameSettings.HostPort"/>, <see cref="GameSettings.HostUpnp"/>). Main thread only.
/// </summary>
internal sealed class HostSession(PauseMenuView pause)
{
    private bool _announced;
    private int _upnpVersion = -1;

    /// <summary>The running server, null while not hosting.</summary>
    internal HostedServer? Server { get; private set; }

    /// <summary>Puts the remembered port and UPnP choice on the host page.</summary>
    internal static void RestoreChoices(MainMenuView view)
    {
        view.Port.Set(GameSettings.HostPort.ToString());
        view.Upnp = GameSettings.HostUpnp;
    }

    /// <summary>Starts hosting <paramref name="target"/> with <paramref name="hostName"/> as its permanent operator;
    /// false with the reason when the server cannot start.</summary>
    internal bool TryStart(HostTarget target, string hostName, out string error)
    {
        Stop();
        Server = HostedServer.TryStart(target.World, target.Port, hostName, target.Upnp, out error);
        if (Server == null)
            return false;
        _announced = false;
        _upnpVersion = -1;
        RefreshLines();
        GameSettings.HostPort = target.Port;
        GameSettings.HostUpnp = target.Upnp;
        GameSettings.Save();
        return true;
    }

    /// <summary>Where the host's own client connects: the server over loopback.</summary>
    internal ConnectTarget Connection => new(Server!.WorldName, Server.LoopbackAddress);

    /// <summary>One server frame, before the client world's; the pause menu's lines follow the UPnP state.</summary>
    internal void Tick(double deltaTime)
    {
        if (Server == null) return;
        Server.Tick(deltaTime);
        if (Server.Upnp is { } upnp && upnp.Version != _upnpVersion)
            RefreshLines();
    }

    // The join address, then the UPnP state when it was asked for; rebuilt only when that state moves
    private void RefreshLines()
    {
        pause.HostLines.Clear();
        pause.HostLines.Add(Server!.JoinLine);
        if (Server.Upnp is not { } upnp) return;
        _upnpVersion = upnp.Version;
        pause.HostLines.Add(upnp.StatusLine);
    }

    /// <summary>Tells the host where others join, once, when it is first in the world.</summary>
    internal void AnnounceOnce(ChatView chat)
    {
        if (Server == null || _announced) return;
        _announced = true;
        chat.AddSystem(Server.JoinLine);
    }

    /// <summary>Stops hosting (saving the world); false when nothing was hosted.</summary>
    internal bool Stop()
    {
        if (Server == null) return false;
        Server.Stop();
        Server = null;
        pause.HostLines.Clear();
        return true;
    }
}
