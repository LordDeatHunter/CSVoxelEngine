using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Commands;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.UI.Hud;

namespace VoxelEngine.App;

/// <summary>Singleplayer's world as commands see it: the one local player, the world's clock, mobs and towers, and
/// the chat log for announcements. Main thread only.</summary>
internal sealed class LocalCommandHost(World world, ChatView chat) : ICommandHost
{
    private readonly LocalCommandPlayer _self = new(world, () => chat.LocalName);

    /// <summary>The local player.</summary>
    public ICommandPlayer Self => _self;

    public IReadOnlyList<string> PlayerNames => [chat.LocalName];

    public ICommandPlayer? FindPlayer(string name) =>
        string.Equals(name, chat.LocalName, StringComparison.OrdinalIgnoreCase) ? _self : null;

    public double LightingTime => world.LightingTime;

    public void SetLightingTime(double seconds) => world.SetLightingTime(seconds);

    public bool SpawnEntity(string name, Vector3 feet) => world.SpawnEntity(name, feet);

    public TowerManager? Towers => world.Towers;

    public void Announce(string text) => chat.AddSystem(text);
}
