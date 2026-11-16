using System.Collections.Generic;
using System.Numerics;

namespace VoxelEngine.Networking.ServerSide;

/// <summary>
/// What the server's components share: the world, the connected clients and players, the
/// connections that have not said hello yet, the server clock, the join spawn and the creative
/// rights (backing <see cref="GameServer.AllowCreative"/> and <see cref="GameServer.Operators"/>).
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
    internal bool AllowCreative { get; set; } = NetworkConfig.ServerAllowCreative;
    internal OperatorList Operators { get; set; } = new();

    /// <summary>Operators run commands whatever the game mode; creative alone grants no command rights.</summary>
    internal bool IsOperator(string name) => Operators.Contains(name);

    internal bool MayBeCreative(string name) => AllowCreative || IsOperator(name);

    internal void Broadcast(byte[] payload)
    {
        foreach (var client in Clients.Values)
            client.Send(payload);
    }
}
