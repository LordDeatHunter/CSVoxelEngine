using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Commands;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.UI.Hud;

namespace VoxelEngine.Networking.Client;

/// <summary>
/// A connected client's world as commands see it, for completing a line only: the names of the players online. A
/// connected client sends every command line to the server, which runs it, so this host acts on nobody and is never
/// asked for an effect. Main thread only.
/// </summary>
internal sealed class ClientCommandHost(World world, ChatView chat) : ICommandHost
{
    public IReadOnlyList<string> PlayerNames
    {
        get
        {
            var names = new List<string>(world.RemotePlayers.Count + 1) { chat.LocalName };
            foreach (var remote in world.RemotePlayers)
                names.Add(remote.Name);
            return names;
        }
    }

    public ICommandPlayer? FindPlayer(string name) => null;

    public double LightingTime => world.LightingTime;

    public void SetLightingTime(double seconds) => throw RunsOnTheServer();

    public bool SpawnEntity(string name, Vector3 feet) => throw RunsOnTheServer();

    public TowerManager? Towers => null;

    public void Announce(string text) => throw RunsOnTheServer();

    private static NotSupportedException RunsOnTheServer() => new("A connected client's commands run on the server.");
}
