using System;
using System.Net;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Saves;

namespace VoxelEngine.Networking.Hosting;

/// <summary>
/// A world hosted from the game: the same <see cref="GameServer"/> over the same headless server-mode
/// <see cref="World"/> the dedicated server runs (<see cref="ServerLaunch"/>), which the hosting player then joins
/// as a client over loopback, so the host plays by the server's rules like everyone else. The server ticks on the
/// game's main thread, once a frame before the client world (<see cref="Tick"/>), exactly as the loopback tests run
/// a server and a client world side by side; its chunk workers run on their own threads as on a dedicated server.
/// The hosting player (<see cref="GameServer.HostPlayer"/>) is always an operator.
/// </summary>
public sealed class HostedServer
{
    private readonly World _world;
    private readonly GameServer _server;

    private HostedServer(WorldInfo world, World serverWorld, GameServer server)
    {
        WorldName = world.Name;
        _world = serverWorld;
        _server = server;
        LanAddress = Hosting.LanAddress.Find();
    }

    /// <summary>The hosted world's name.</summary>
    public string WorldName { get; }

    /// <summary>The port the server listens on (the one asked for, or the free one picked for port 0).</summary>
    public int Port => _server.BoundPort;

    /// <summary>The address other players on this network join at, or null when this machine has none.</summary>
    public IPAddress? LanAddress { get; }

    /// <summary>Whether the server has stopped (<see cref="Stop"/>, or a tick that failed).</summary>
    public bool Stopped { get; private set; }

    /// <summary>Where others join, for the pause menu and the chat: the LAN address and port.</summary>
    public string JoinLine => LanAddress != null
        ? $"Hosting on {LanAddress}:{Port}"
        : $"Hosting on port {Port} (no network address found)";

    /// <summary>The address the hosting player's own client connects to.</summary>
    public ServerAddress LoopbackAddress => new(IPAddress.Loopback.ToString(), Port);

    /// <summary>
    /// Opens <paramref name="world"/> as a server on <paramref name="port"/> with <paramref name="hostPlayer"/> as
    /// its permanent operator; null with the reason in <paramref name="error"/> when it cannot (the port in use,
    /// the world open elsewhere, an old save format). Main thread only, and only while no world is live.
    /// </summary>
    public static HostedServer? TryStart(WorldInfo world, int port, string hostPlayer, out string error)
    {
        var launch = ServerLaunch.Start(world, port, new OperatorList(), hostPlayer: hostPlayer);
        error = launch.Message;
        if (!launch.Started)
            return null;
        Log.WriteLine($"Hosting '{world.Name}' on port {launch.Server!.BoundPort}");
        return new HostedServer(world, launch.World!, launch.Server);
    }

    /// <summary>
    /// One server frame: the world's simulation, then the network pump. A failure stops the server (saving the
    /// world, as the dedicated server's loop does on an exception) instead of taking the game down; the host's
    /// client then sees the connection end and returns to the menu.
    /// </summary>
    public void Tick(double deltaTime)
    {
        if (Stopped) return;
        try
        {
            _world.Update(deltaTime, null);
            _server.Pump(deltaTime);
        }
        catch (Exception e)
        {
            Log.WriteLine($"The hosted server failed and is stopping: {e}", ConsoleColor.Red);
            Stop();
        }
    }

    /// <summary>Closes every connection, saves the players and the world and releases its folder. Safe to call
    /// more than once.</summary>
    public void Stop()
    {
        if (Stopped) return;
        Stopped = true;
        _server.Stop();
        _world.SaveAndDispose();
        Log.WriteLine($"Stopped hosting '{WorldName}'");
    }
}
