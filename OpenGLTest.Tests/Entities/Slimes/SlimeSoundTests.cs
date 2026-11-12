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
/// A slime's sounds: a hop it launches plays the slime jump group, and a landing the land group, through the
/// manager's <see cref="SlimeManager.PlaySound"/> hook, from where the slime is.
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
        var at = Vector3.Zero;
        var manager = new SlimeManager(EntityRegistry.Require("slime"));
        var drops = new DropManager();
        var target = new Target(new Vector3(6.5f, 1f + Camera.EyeHeight, 6.5f));
        var slime = manager.Spawn(new Vector3(2.5f, 1.02f + Slime.HalfExtent, 2.5f))!;
        manager.PlaySound = (group, position) =>
        {
            played.Add(group);
            if (group == SfxLibrary.SlimeJumpGroup) at = position;
        };

        for (var t = 0f; t < 6f && !played.Contains(SfxLibrary.SlimeJumpGroup); t += Dt)
            manager.Update(Dt, chunks, drops, [target]);

        Assert.Contains(SfxLibrary.SlimeJumpGroup, played);
        // Heard from the slime that hopped, not from the player it hopped at
        Assert.True(Vector3.Distance(at, slime.Position) < 1f);
    }

    [Fact]
    public void ASlimeComingDownOntoTheGroundPlaysTheLandGroup()
    {
        var chunks = MobTestWorld.Floor(Stone);
        var played = new List<string>();
        var manager = new SlimeManager(EntityRegistry.Require("slime")) { PlaySound = (group, _) => played.Add(group) };
        var drops = new DropManager();
        var target = new Target(new Vector3(6.5f, 1f + Camera.EyeHeight, 6.5f));
        // Dropped from well above the floor, so its first touchdown is a landing
        manager.Spawn(new Vector3(2.5f, 5f, 2.5f));

        for (var t = 0f; t < 3f && !played.Contains(SfxLibrary.SlimeLandGroup); t += Dt)
            manager.Update(Dt, chunks, drops, [target]);

        Assert.Contains(SfxLibrary.SlimeLandGroup, played);
    }
}
