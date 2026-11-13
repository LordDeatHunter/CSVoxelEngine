using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Simulation.Tower;

namespace VoxelEngine.Commands;

/// <summary>
/// The world a command runs in: singleplayer's <c>World</c>, a server's state, or a connected client that only
/// completes lines (its <see cref="FindPlayer"/> finds nobody and its effects are never asked for, since a connected
/// client sends every command line to the server).
/// </summary>
public interface ICommandHost
{
    /// <summary>The names of the players online, for player arguments.</summary>
    IReadOnlyList<string> PlayerNames { get; }

    /// <summary>The online player named exactly <paramref name="name"/> (case-insensitive), or null.</summary>
    ICommandPlayer? FindPlayer(string name);

    /// <summary>The lighting clock, in seconds.</summary>
    double LightingTime { get; }

    /// <summary>Sets the lighting clock for everyone.</summary>
    void SetLightingTime(double seconds);

    /// <summary>Spawns one mob of the named entity kind with its feet at <paramref name="feet"/>; false when it
    /// cannot (an unspawnable kind, a full mob cap).</summary>
    bool SpawnEntity(string name, Vector3 feet);

    /// <summary>The battle towers, so <c>/locate</c> skips a conquered one.</summary>
    TowerManager? Towers { get; }

    /// <summary>A system line every player sees.</summary>
    void Announce(string text);
}
