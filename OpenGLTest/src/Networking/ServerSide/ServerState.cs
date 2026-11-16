using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Saves;

namespace VoxelEngine.Networking.ServerSide;

/// <summary>
/// What the server's components share: the world, the connected clients and players, the
/// connections that have not said hello yet, the server clock, the join spawn and the operators
/// (backing <see cref="GameServer.Operators"/>).
/// Main thread only.
/// </summary>
internal sealed class ServerState(World world)
{
    internal World World { get; } = world;
    internal Dictionary<int, ClientConnection> Clients { get; } = new();
    internal Dictionary<int, ServerPlayer> Players { get; } = new();
    // The same players as a list, handed to the world once (World.ServerPlayers) for its pickup
    internal List<ServerPlayer> PlayerList { get; } = [];
    internal List<ClientConnection> Pending { get; } = [];
    // Where joiners spawn: the server camera's position at the first hello. The camera never
    // follows the players (chunks stream around each of them through World.Focus; the camera is
    // the focus only while nobody is online), so it still stands at the world spawn, and later
    // joiners never inherit where whoever is online happens to be, which can sit inside rock.
    internal Vector3? JoinSpawn { get; set; }
    internal double Elapsed { get; set; }
    internal OperatorList Operators { get; set; } = new();

    /// <summary>The player hosting this world from the game, an operator who is never in
    /// <see cref="Operators"/> (so never saved there) and cannot be deopped; null on a dedicated server.</summary>
    internal string? HostPlayer { get; set; }

    /// <summary>Operators run commands whatever the game mode and are the only players who may be creative;
    /// creative alone grants no command rights.</summary>
    internal bool IsOperator(string name) => IsHost(name) || Operators.Contains(name);

    /// <summary>Whether <paramref name="name"/> is the <see cref="HostPlayer"/> (save-slug match).</summary>
    internal bool IsHost(string name) =>
        !string.IsNullOrWhiteSpace(HostPlayer) && !string.IsNullOrWhiteSpace(name)
        && WorldNames.Slug(HostPlayer.Trim()) == WorldNames.Slug(name.Trim());

    internal void Broadcast(byte[] payload)
    {
        foreach (var client in Clients.Values)
            client.Send(payload);
    }
}
