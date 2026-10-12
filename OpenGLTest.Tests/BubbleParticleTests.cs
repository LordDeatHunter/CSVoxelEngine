using System;
using System.Collections.Generic;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class BubbleParticleTests
{
    public BubbleParticleTests() => TestContent.EnsureLoaded();

    private static byte WaterId => ContentRegistry.RequireBlock("water").Id;
    private static byte StoneId => ContentRegistry.RequireBlock("stone").Id;

    private static (ChunkManager Chunks, Chunk Center) WaterColumn(int top = 12)
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, 0, z, StoneId, 0);
        for (var x = 4; x < 12; x++)
        for (var z = 4; z < 12; z++)
        for (var y = 1; y <= top; y++)
            center.SetBlockRaw(x, y, z, WaterId, FluidState.Source);
        return (manager, center);
    }

    [Fact]
    public void BubbleRisesUpwards()
    {
        var (chunks, _) = WaterColumn();
        var bubble = new BubbleParticle(new Vector3(8.5f, 3f, 8.5f), new Random(1));
        var startY = bubble.Position.Y;

        Assert.True(bubble.Update(0.5f, chunks));
        Assert.True(bubble.Position.Y > startY);
    }

    [Fact]
    public void BubblePopsAtTheSurface()
    {
        var (chunks, _) = WaterColumn(top: 5);
        var bubble = new BubbleParticle(new Vector3(8.5f, 4.5f, 8.5f), new Random(2));

        var alive = true;
        for (var i = 0; i < 600 && alive; i++)
            alive = bubble.Update(1f / 60f, chunks);

        Assert.False(alive);
    }

    [Fact]
    public void BubbleAboveWaterPopsImmediately()
    {
        var (chunks, _) = WaterColumn(top: 5);
        var bubble = new BubbleParticle(new Vector3(8.5f, 7f, 8.5f), new Random(3));

        Assert.False(bubble.Update(1f / 60f, chunks));
    }

    [Fact]
    public void BubbleDiesAfterItsLifetime()
    {
        var (chunks, _) = WaterColumn(top: 15);
        var system = new BubbleParticleSystem();
        system.AddForTest(new BubbleParticle(new Vector3(8.5f, 2f, 8.5f), new Random(4)));

        // Far past BubbleLifetime: whatever it hits first (surface or timeout), it is gone.
        for (var i = 0; i < 1200 && system.Particles.Count > 0; i++)
            system.Update(1f / 60f, new Vector3(8.5f, 3f, 8.5f), -Vector3.UnitZ,
                false, chunks, new Random(4));

        Assert.Empty(system.Particles);
    }

    [Fact]
    public void SystemSpawnsOnlyWhileUnderwater()
    {
        var (chunks, _) = WaterColumn();
        var eye = new Vector3(8.5f, 5f, 8.5f);
        var rng = new Random(5);

        var dry = new BubbleParticleSystem();
        for (var i = 0; i < 120; i++)
            dry.Update(1f / 60f, eye, -Vector3.UnitZ, false, chunks, rng);
        Assert.Empty(dry.Particles);

        var wet = new BubbleParticleSystem();
        for (var i = 0; i < 120; i++)
            wet.Update(1f / 60f, eye, -Vector3.UnitZ, true, chunks, rng);
        Assert.NotEmpty(wet.Particles);
    }

    [Fact]
    public void EachSwimStrokeLetsOutOneBurstWhileTheEyeIsUnderwater()
    {
        var system = new BubbleParticleSystem();
        var head = new Vector3(8.5f, 5f, 8.5f);
        var facing = -Vector3.UnitZ;
        var rng = new Random(21);

        // The first call only learns the counter: a world joined mid-swim does not burst at once.
        Assert.Equal(0, system.TrackSwimStrokes(7, head, facing, eyeInWater: true, rng));

        // A stroke bursts once; the same counter on later frames lets out nothing more.
        var burst = system.TrackSwimStrokes(8, head, facing, eyeInWater: true, rng);
        Assert.True(burst > 0);
        Assert.Equal(burst, system.Particles.Count);
        Assert.Equal(0, system.TrackSwimStrokes(8, head, facing, eyeInWater: true, rng));

        // A stroke with the eye dry spawns nothing, and is not saved up for when it goes under.
        Assert.Equal(0, system.TrackSwimStrokes(9, head, facing, eyeInWater: false, rng));
        Assert.Equal(0, system.TrackSwimStrokes(9, head, facing, eyeInWater: true, rng));

        // The next stroke under water bursts again, by the same amount.
        Assert.Equal(burst, system.TrackSwimStrokes(10, head, facing, eyeInWater: true, rng));
        Assert.Equal(2 * burst, system.Particles.Count);
    }

    [Fact]
    public void AStrokeBurstLeavesFromJustInFrontOfTheHead()
    {
        var system = new BubbleParticleSystem();
        var head = new Vector3(8.5f, 5f, 8.5f);
        var facing = new Vector3(1f, 0f, 0f);
        var rng = new Random(22);
        system.TrackSwimStrokes(0, head, facing, eyeInWater: true, rng);
        system.TrackSwimStrokes(1, head, facing, eyeInWater: true, rng);

        Assert.NotEmpty(system.Particles);
        foreach (var bubble in system.Particles)
        {
            var offset = bubble.Position - head;
            Assert.True(Vector3.Dot(offset, facing) > 0f);
            Assert.True(offset.Length() < 1f);
        }
    }

    [Fact]
    public void SystemRespectsMaxParticles()
    {
        var (chunks, _) = WaterColumn(top: 15);
        var system = new BubbleParticleSystem();
        var rng = new Random(9);
        for (var i = 0; i < 3600; i++)
            system.Update(1f / 60f, new Vector3(8.5f, 5f, 8.5f), -Vector3.UnitZ,
                true, chunks, rng);

        Assert.True(system.Particles.Count <= WorldGenerationConfig.BubbleParticleMax);
        Assert.NotEmpty(system.Particles);
    }

    /// <summary>
    /// C12(a): eviction at the cap drops the OLDEST bubble and leaves every survivor in spawn
    /// order. SystemRespectsMaxParticles above pins only the count and would pass a swap-remove
    /// that reordered the sequence. BubblePerSecond is 1 and the lifetime is 4 s, so the cap is
    /// not reachable by driving Update alone; the fill uses AddForTest (a bare append with no
    /// eviction, exactly as it was over the List this replaces) and then ONE real Update supplies
    /// the spawn that forces the eviction through the production path.
    ///
    /// Identity is by REFERENCE, not by position: unlike the leaf and debris facts this one does
    /// drive Update, so every position moves. One second of debt at BubblePerSecond = 1 spawns
    /// exactly one bubble, and every pre-filled bubble survives that step - age 1 s against a
    /// lifetime of at least BubbleLifetime * 0.7 = 2.8 s, and a rise of BubbleRise plus wobble
    /// under 0.7 blocks against a surface 14 blocks above - so the survivor set is exactly the
    /// fill minus its head, with the new bubble at the tail.
    /// </summary>
    [Fact]
    public void EvictionDropsTheOldestBubbleAndKeepsTheRestInSpawnOrder()
    {
        var (chunks, _) = WaterColumn(top: 15);
        var max = WorldGenerationConfig.BubbleParticleMax;
        var system = new BubbleParticleSystem();
        var rng = new Random(13);

        var filled = new List<BubbleParticle>();
        for (var i = 0; i < max; i++)
        {
            var bubble = new BubbleParticle(new Vector3(8.5f, 2f, 8.5f), rng);
            filled.Add(bubble);
            system.AddForTest(bubble);
        }
        Assert.Equal(max, system.Particles.Count);

        system.Update(1f, new Vector3(8.5f, 3f, 8.5f), -Vector3.UnitZ, true, chunks, rng);

        Assert.Equal(max, system.Particles.Count);
        for (var i = 0; i < max - 1; i++)
            Assert.Same(filled[i + 1], system.Particles[i]);
        Assert.DoesNotContain(filled[0], system.Particles);
        // Anti-vacuity: the tail really is a newly spawned bubble, not the fill shifted along.
        Assert.NotSame(filled[max - 1], system.Particles[max - 1]);
    }
}
