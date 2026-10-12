using System;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The slime block's bounce: a body that lands on one is sprung back up and never billed fall
/// damage (the tracker's cushioned landing), where the same fall onto stone stops dead.
/// </summary>
public sealed class SlimeBlockTests
{
    public SlimeBlockTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static ChunkManager Floor(byte topId)
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, topId, 0);
        manager.Chunks[Vector3.Zero] = chunk;
        return manager;
    }

    // Drops a body from 5 blocks onto the floor and reports the fastest it rose again and whether
    // it came to rest.
    private static (float Rebound, bool Rested) Drop(byte topId)
    {
        var manager = Floor(topId);
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(2.5f, 5f + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
        var keys = new ScriptedKeyboard();

        var rebound = 0f;
        var fell = false;
        for (var i = 0; i < 600; i++)
        {
            camera.Update(Dt, keys, manager);
            if (camera.VerticalVelocity < -1f) fell = true;
            if (fell && camera.VerticalVelocity > rebound) rebound = camera.VerticalVelocity;
        }
        return (rebound, camera.IsGrounded);
    }

    [Fact]
    public void ALandingOnASlimeBlockSpringsBackUpAndSettles()
    {
        var (rebound, rested) = Drop(ContentRegistry.RequireBlock("slime_block").Id);
        Assert.True(rebound > 1f, $"the body never bounced: {rebound}");
        Assert.True(rested, "the bounces never died out");
    }

    [Fact]
    public void TheSameFallOntoStoneStopsDead()
    {
        var (rebound, rested) = Drop(ContentRegistry.RequireBlock("stone").Id);
        Assert.Equal(0f, rebound);
        Assert.True(rested);
    }

    [Fact]
    public void ACushionedLandingBillsNoDamageButStillLands()
    {
        // A 9-block fall in frame-sized steps (a single big step would read as a teleport)
        static void Fall(FallTracker tracker)
        {
            for (var feet = 10f; feet > 1f; feet -= 0.25f)
                tracker.Observe(feet, grounded: false, flying: false, inWater: false, creative: false,
                    FallTracker.DefaultMaxStepBlocks);
        }

        var tracker = new FallTracker();
        Fall(tracker);
        var damage = tracker.Observe(1f, grounded: true, flying: false, inWater: false, creative: false,
            FallTracker.DefaultMaxStepBlocks, cushioned: true);
        Assert.Equal(0, damage);
        Assert.Equal(9f, tracker.LandedDrop, 3);

        var hard = new FallTracker();
        Fall(hard);
        Assert.True(hard.Observe(1f, grounded: true, flying: false, inWater: false, creative: false,
            FallTracker.DefaultMaxStepBlocks) > 0);
    }

    [Fact]
    public void OnlyTheSlimeBlockCarriesTheBounceFlag()
    {
        Assert.True(BlockType.BouncesAt(ContentRegistry.RequireBlock("slime_block").Id));
        Assert.False(BlockType.BouncesAt(ContentRegistry.RequireBlock("stone").Id));
        Assert.False(BlockType.BouncesAt(BlockType.Air));
    }
}
