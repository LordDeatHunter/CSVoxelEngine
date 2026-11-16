using System;
using System.IO;
using System.Net.Sockets;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Saves;

namespace VoxelEngine.Networking.Hosting;

/// <summary>Why <see cref="ServerLaunch.Start"/> did not start a server.</summary>
public enum ServerLaunchFailure
{
    None,
    /// <summary>The world was saved by another version (<see cref="ServerLaunch.IsHostable"/>).</summary>
    OldFormat,
    /// <summary>Another world is live in this process, so the seed cannot be applied.</summary>
    WorldLive,
    /// <summary>The world source could not be built (unknown name, missing or broken map).</summary>
    BadSource,
    /// <summary>The world folder is open in another session (its <c>session.lock</c>).</summary>
    Locked,
    /// <summary>The port could not be listened on, most often because it is in use.</summary>
    PortInUse,
}

/// <summary>A started server and its headless world, or why none started (<see cref="Message"/> says it for a
/// person).</summary>
public sealed record ServerLaunchResult(
    World? World,
    GameServer? Server,
    IWorldSource? Source,
    ServerLaunchFailure Failure,
    string Message)
{
    public bool Started => Failure == ServerLaunchFailure.None;
}

/// <summary>
/// Opens a saved world as a server: the one setup the dedicated server and a world hosted from the game share. In
/// order: the save-format gate, the world source (built before the folder is locked, so a refused source leaves it
/// untouched), the save store (which takes the folder's lock), the operators (<paramref name="operators"/> plus the
/// world's <see cref="OperatorList.FileName"/>), the seed, the headless server-mode <see cref="World"/>, and the
/// listening <see cref="GameServer"/>. A failure after the world is built saves and closes it, so nothing is left
/// live or locked. Main thread only, and only while no other world is live.
/// </summary>
public static class ServerLaunch
{
    /// <summary>Whether this build reads <paramref name="world"/>'s save format (the menu's PLAY gate).</summary>
    public static bool IsHostable(WorldInfo world) => world.Version == WorldStore.CurrentVersion;

    /// <summary>
    /// Starts <paramref name="world"/> on <paramref name="port"/> (0 picks a free one). <paramref name="sourceName"/>
    /// overrides the world's own <see cref="WorldInfo.Source"/> when not null; <paramref name="hostPlayer"/> names
    /// the player hosting from the game (<see cref="GameServer.HostPlayer"/>), null on a dedicated server.
    /// </summary>
    public static ServerLaunchResult Start(WorldInfo world, int port, OperatorList operators,
        string? sourceName = null, string? hostPlayer = null)
    {
        if (!IsHostable(world))
            return Fail(ServerLaunchFailure.OldFormat,
                $"'{world.Name}' was saved by an older version (format {world.Version}, this build reads " +
                $"{WorldStore.CurrentVersion}); it cannot be hosted.");
        if (WorldSeed.WorldIsLive)
            return Fail(ServerLaunchFailure.WorldLive, "A world is already live in this process; cannot start a second.");

        sourceName ??= world.Source;
        IWorldSource source;
        try
        {
            source = WorldSources.Create(sourceName, world.Directory);
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException
                                      or UnauthorizedAccessException)
        {
            return Fail(ServerLaunchFailure.BadSource, $"Cannot use world source '{sourceName}': {e.Message}");
        }

        ChunkSaveStore save;
        try
        {
            // The store takes the folder's session lock: a world the game or another server still has open is
            // refused here, before anything could race its writer.
            save = new ChunkSaveStore(world.Directory);
        }
        catch (IOException e)
        {
            return Fail(ServerLaunchFailure.Locked,
                $"'{world.Name}' is open in another session (the game or a server); close it first. ({e.Message})");
        }

        operators.AddAll(OperatorList.Parse(save.ReadFile(OperatorList.FileName)));

        WorldSeed.Apply(world.Seed);
        var gameWorld = new World(null!, null!, null!, null!, null!, null!, null, save, serverMode: true,
            worldSource: source);
        var server = new GameServer(gameWorld, world.Name, world.Seed, port)
        {
            Operators = operators,
            HostPlayer = hostPlayer,
        };
        try
        {
            server.Start();
        }
        catch (SocketException e)
        {
            // Nothing was served yet, but the world was opened, so it is closed cleanly
            server.Dispose();
            gameWorld.SaveAndDispose();
            return Fail(ServerLaunchFailure.PortInUse, $"Port {port} is unavailable ({e.Message})");
        }
        return new ServerLaunchResult(gameWorld, server, source, ServerLaunchFailure.None, "");
    }

    private static ServerLaunchResult Fail(ServerLaunchFailure failure, string message) =>
        new(null, null, null, failure, message);
}
