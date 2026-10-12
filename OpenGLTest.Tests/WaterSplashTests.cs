using System;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The water splash: the droplet rule (<see cref="WaterSplash.Count"/>), the per-body crossing
/// memory (<see cref="WaterSplashTracker"/>) and the droplets <see cref="WaterSplash.Track"/> throws
/// off a real pool.
/// </summary>
public sealed class WaterSplashTests
{
    public WaterSplashTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    [Fact]
    public void ASlowCrossingSplashesNothingAndTheCountNeverFallsAsTheSpeedGrows()
    {
        Assert.Equal(0, WaterSplash.Count(0f));
        Assert.Equal(0, WaterSplash.Count(WorldGenerationConfig.WaterSplashMinSpeed * 0.5f));

        foreach (var scale in new[] { WorldGenerationConfig.WaterSplashSmallScale, 1f, WorldGenerationConfig.WaterSplashCubeScale })
        {
            var lastCount = 0;
            var lastSpeed = 0f;
            for (var speed = 0f; speed <= WorldGenerationConfig.WaterSplashFullSpeed * 2f; speed += 0.25f)
            {
                var count = WaterSplash.Count(speed, scale);
                var launch = WaterSplash.Speed(speed);
                Assert.True(count >= lastCount, $"fewer droplets at {speed} b/s (scale {scale})");
                Assert.True(launch >= lastSpeed, $"slower droplets at {speed} b/s");
                lastCount = count;
                lastSpeed = launch;
            }
        }
    }

    [Fact]
    public void ABiggerBodyNeverThrowsFewerDroplets()
    {
        for (var speed = 0f; speed <= WorldGenerationConfig.WaterSplashFullSpeed; speed += 0.5f)
            Assert.True(WaterSplash.Count(speed, 2f) >= WaterSplash.Count(speed, 1f));
    }

    [Fact]
    public void AnEntryReportsItsDownwardSpeedAndAnExitItsUpwardSpeed()
    {
        var tracker = new WaterSplashTracker();
        // Seeded dry; falling at 10 b/s into the water
        Assert.False(tracker.Step(false, 12f, Dt, out _));
        Assert.False(tracker.Step(false, 12f - 10f * Dt, Dt, out _));
        Assert.True(tracker.Step(true, 12f - 20f * Dt, Dt, out var entry));
        Assert.Equal(10f, entry, 2);

        // Bobbing about under water splashes nothing; then out at 6 b/s
        Assert.False(tracker.Step(true, 11f, Dt, out _));
        Assert.True(tracker.Step(false, 11f + 6f * Dt, Dt, out var exit));
        Assert.Equal(6f, exit, 2);
    }

    [Fact]
    public void ABodyThatAppearsTeleportsOrSitsStillSplashesNothing()
    {
        // Appearing already in water only seeds
        var fresh = new WaterSplashTracker();
        Assert.False(fresh.Step(true, 5f, Dt, out _));

        // A step farther than the teleport distance across the surface
        var teleported = new WaterSplashTracker();
        teleported.Step(false, 40f, Dt, out _);
        Assert.False(teleported.Step(true, 5f, Dt, out _));

        // Resting on dry ground until water rises over it
        var resting = new WaterSplashTracker();
        resting.Step(false, 8f, Dt, out _);
        resting.Step(false, 7f, Dt, out _);
        for (var t = 0f; t < WorldGenerationConfig.WaterSplashStaleSeconds * 2f; t += Dt)
            resting.Step(false, 7f, Dt, out _);
        Assert.False(resting.Step(true, 7f, Dt, out _));
    }

    [Fact]
    public void AMirrorThatOnlyMovesPerSnapshotReadsTheSpeedOverTheSnapshot()
    {
        // A client mirror's height jumps once every 0.1 s: 0.6 blocks per snapshot is 6 b/s, not 36
        var tracker = new WaterSplashTracker();
        tracker.Step(false, 10f, Dt, out _);
        for (var i = 0; i < 5; i++)
            tracker.Step(false, 10f, Dt, out _);
        Assert.True(tracker.Step(true, 9.4f, Dt, out var speed));
        Assert.Equal(6f, speed, 1);
    }

    [Fact]
    public void ABodyFallingIntoAPoolThrowsDropletsOffItsTopButLavaThrowsNone()
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        var chunk = manager.Chunks[Vector3.Zero];
        var water = ContentRegistry.RequireBlock("water").Id;
        var lava = ContentRegistry.RequireBlock("lava").Id;
        for (var x = 2; x < 6; x++)
        for (var y = 0; y < 4; y++)
        for (var z = 2; z < 6; z++)
            chunk.SetBlockRaw(x, y, z, water, FluidState.Source);
        for (var x = 10; x < 14; x++)
        for (var y = 0; y < 4; y++)
        for (var z = 10; z < 14; z++)
            chunk.SetBlockRaw(x, y, z, lava, FluidState.Source);

        var debris = new BlockDebrisSystem();
        Assert.True(Fall(new Vector2(4.5f, 4.5f), manager, debris) > 0);
        var top = manager.GetFluidColumnTopGlobal(4, 2, 4);
        foreach (var droplet in debris.Debris)
        {
            Assert.Equal(DebrisLook.Color, droplet.Look);
            Assert.True(MathF.Abs(droplet.Position.Y - top) < 0.01f);
        }

        Assert.Equal(0, Fall(new Vector2(12.5f, 12.5f), manager, new BlockDebrisSystem()));
    }

    // Drops a body at 12 b/s from y 6 to y 2 over the column (x, z); returns the droplets it threw
    private static int Fall(Vector2 column, ChunkManager manager, BlockDebrisSystem debris)
    {
        var tracker = new WaterSplashTracker();
        var thrown = 0;
        for (var y = 6f; y > 2f; y -= 12f * Dt)
            thrown += WaterSplash.Track(tracker, new Vector3(column.X, y, column.Y), Dt, 1f, manager, debris);
        return thrown;
    }
}
