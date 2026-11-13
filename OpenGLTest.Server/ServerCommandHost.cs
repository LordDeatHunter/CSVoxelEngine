using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Commands;
using VoxelEngine.Simulation.Tower;

namespace VoxelEngine.Server;

/// <summary>The server's world as commands see it: the connected players, the world's clock (a change goes out to
/// every client at once), mobs and towers, and announcements broadcast as system lines. Main thread only.</summary>
internal sealed class ServerCommandHost(ServerState state, ServerBroadcasts broadcasts, Action<string> announce) : ICommandHost
{
    public IReadOnlyList<string> PlayerNames
    {
        get
        {
            var names = new List<string>(state.PlayerList.Count);
            foreach (var player in state.PlayerList)
                names.Add(player.Name);
            return names;
        }
    }

    public ICommandPlayer? FindPlayer(string name)
    {
        foreach (var player in state.PlayerList)
        {
            if (!string.Equals(player.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return state.Clients.TryGetValue(player.Id, out var client) ? new ServerCommandPlayer(client, player, state) : null;
        }
        return null;
    }

    public double LightingTime => state.World.LightingTime;

    public void SetLightingTime(double seconds)
    {
        state.World.SetLightingTime(seconds);
        broadcasts.BroadcastTime();
    }

    public bool SpawnEntity(string name, Vector3 feet) => state.World.SpawnEntity(name, feet);

    public TowerManager? Towers => state.World.Towers;

    public void Announce(string text) => announce(text);
}
