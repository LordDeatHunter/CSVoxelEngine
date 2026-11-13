using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Commands;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.UI.Hud;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// A headless command world for the command tests: a list of <see cref="RecordingCommandPlayer"/>s, a clock, and a
/// record of spawns, announcements and the replies each <see cref="SourceFor"/> source received.
/// </summary>
public sealed class RecordingCommandHost : ICommandHost
{
    public List<RecordingCommandPlayer> Players { get; } = [];

    public List<(string Entity, Vector3 Feet)> Spawned { get; } = [];

    public List<string> Announced { get; } = [];

    /// <summary>Every reply sent to any source built here, in order.</summary>
    public List<(string Text, ChatLink? Link)> Replies { get; } = [];

    public IReadOnlyList<string> PlayerNames => Players.Select(p => p.Name).ToList();

    public ICommandPlayer? FindPlayer(string name) =>
        Players.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public double LightingTime { get; private set; }

    public void SetLightingTime(double seconds) => LightingTime = seconds;

    public bool SpawnEntity(string name, Vector3 feet)
    {
        Spawned.Add((name, feet));
        return true;
    }

    public TowerManager? Towers => null;

    public void Announce(string text) => Announced.Add(text);

    /// <summary>A player added to this world.</summary>
    public RecordingCommandPlayer Add(string name, Vector3 feet = default, bool mayBeCreative = true)
    {
        var player = new RecordingCommandPlayer(name, feet) { MayBeCreative = mayBeCreative };
        Players.Add(player);
        return player;
    }

    /// <summary>A source acting as <paramref name="self"/> with the given rights; its replies land in
    /// <see cref="Replies"/>.</summary>
    public CommandSource SourceFor(RecordingCommandPlayer self, CommandPermission permission = CommandPermission.Operator) =>
        new(self.Name, permission, self.Feet, self, this, (text, link) => Replies.Add((text, link)));

    /// <summary>The newest reply's text.</summary>
    public string LastReply => Replies.Count == 0 ? "" : Replies[^1].Text;
}
