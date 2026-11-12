using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Content;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Entities.Slimes;

/// <summary>
/// A slime's sounds: a hop it launches plays the slime jump group through the manager's
/// <see cref="SlimeManager.PlaySound"/> hook, and a headless manager (no hook) plays nothing.
/// </summary>
public sealed class SlimeSoundTests
{
    public SlimeSoundTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private sealed class Target(Vector3 eye) : IMobTarget
    {
        public Vector3 Eye { get; } = eye;
        public bool IsImmune => false;
        public void TakeHit(int damage, Vector3 awayHorizontal, float knockbackSpeed, float lift, string? attacker = null) { }
    }

    private static ushort Stone => ContentRegistry.RequireBlock("stone").Id;

    [Fact]
    public void ASlimeHopPlaysTheJumpGroupThroughTheHook()
    {
        var chunks = MobTestWorld.Floor(Stone);
        var played = new List<string>();
        var manager = new SlimeManager(EntityRegistry.Require("slime")) { PlaySound = played.Add };
        var drops = new DropManager();
        var target = new Target(new Vector3(6.5f, 1f + Camera.EyeHeight, 6.5f));
        manager.Spawn(new Vector3(2.5f, 1.02f + Slime.HalfExtent, 2.5f));

        for (var t = 0f; t < 6f && played.Count == 0; t += Dt)
            manager.Update(Dt, chunks, drops, [target]);

        Assert.Contains(SfxLibrary.SlimeJumpGroup, played);
    }
}
