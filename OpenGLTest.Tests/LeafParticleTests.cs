using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class LeafParticleTests
{
    public LeafParticleTests() => TestContent.EnsureLoaded();

    private static byte LeavesId => ContentRegistry.RequireBlock("oak_leaves").Id;
    private static byte StoneId => ContentRegistry.RequireBlock("stone").Id;

    private static (ChunkManager Chunks, Chunk Center) Setup()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, 0, z, StoneId, 0);
        return (manager, center);
    }

    [Fact]
    public void CropIsSixTexelsCentredOnTheTile()
    {
        var crop = LeafParticleRenderer.CenterCrop(7, 32, 6);
        Assert.Equal(7, crop.Layer);
        Assert.Equal(13f / 32f, crop.Min.X, 5);
        Assert.Equal(13f / 32f, crop.Min.Y, 5);
        Assert.Equal(19f / 32f, crop.Max.X, 5);
        Assert.Equal(19f / 32f, crop.Max.Y, 5);
    }

    [Fact]
    public void LeafSinksSwaysAndLandsOnTheFloor()
    {
        var (chunks, _) = Setup();
        var leaf = new LeafParticle(new Vector3(8.5f, 5.5f, 8.5f), random: new Random(1));
        var startX = leaf.Position.X;

        var landed = false;
        for (var i = 0; i < 600 && !landed; i++)
        {
            Assert.True(leaf.Update(1f / 60f, chunks));
            landed = leaf.IsLanded;
        }

        Assert.True(landed);
        Assert.True(leaf.Position.Y < 5.5f);
        Assert.NotEqual(startX, leaf.Position.X);
    }

    [Fact]
    public void AFallingLeafDriftsDownTheWindWhicheverWayItBlows()
    {
        var (chunks, _) = Setup();
        foreach (var wind in new[] { Vector2.UnitX, -Vector2.UnitY, Vector2.Normalize(new Vector2(-0.6f, 0.8f)) })
        {
            // Same seed, so the two leaves differ only by the wind
            var still = new LeafParticle(new Vector3(8.5f, 6.5f, 8.5f), random: new Random(9));
            var blown = new LeafParticle(new Vector3(8.5f, 6.5f, 8.5f), random: new Random(9));
            for (var i = 0; i < 180; i++)
            {
                Assert.True(still.Update(1f / 60f, chunks));
                Assert.True(blown.Update(1f / 60f, chunks, wind, gust: 1f));
            }

            Assert.False(blown.IsLanded, "the leaf landed before the comparison");
            var drift = blown.Position - still.Position;
            var along = drift.X * wind.X + drift.Z * wind.Y;
            Assert.True(along > 0.5f, $"wind {wind}: drifted {along} along it");
            // It still falls as a leaf, not faster or slower for the wind
            Assert.Equal(still.Position.Y, blown.Position.Y, 3);
        }
    }

    [Fact]
    public void AHarderGustCarriesALeafFurther()
    {
        var (chunks, _) = Setup();
        var soft = new LeafParticle(new Vector3(8.5f, 6.5f, 8.5f), random: new Random(10));
        var hard = new LeafParticle(new Vector3(8.5f, 6.5f, 8.5f), random: new Random(10));
        for (var i = 0; i < 120; i++)
        {
            soft.Update(1f / 60f, chunks, Vector2.UnitX, gust: 0.3f);
            hard.Update(1f / 60f, chunks, Vector2.UnitX, gust: 1f);
        }
        Assert.True(hard.Position.X > soft.Position.X);
    }

    [Fact]
    public void ALandedLeafStaysPutInTheWind()
    {
        var (chunks, _) = Setup();
        var leaf = new LeafParticle(new Vector3(8.5f, 2.5f, 8.5f), random: new Random(11));
        for (var i = 0; i < 600 && !leaf.IsLanded; i++)
            leaf.Update(1f / 60f, chunks, Vector2.UnitX, gust: 1f);
        Assert.True(leaf.IsLanded);

        var rest = leaf.Position;
        for (var i = 0; i < 30 && leaf.IsAlive; i++)
            leaf.Update(1f / 60f, chunks, Vector2.UnitX, gust: 1f);
        Assert.Equal(rest, leaf.Position);
    }

    [Fact]
    public void TheSystemBlowsItsLeavesAlongTheWindOfItsClock()
    {
        var (chunks, _) = Setup();
        // A day cycle apart the sun stands the same and the wind has turned right round
        foreach (var t in new[] { 147.0, 147.0 + 180.0 / WorldGenerationConfig.WindTurnDegreesPerSecond })
        {
            var system = new LeafParticleSystem();
            var rng = new Random(12);
            for (var i = 0; i < 20; i++)
                system.AddForTest(new LeafParticle(new Vector3(8.5f, 7.5f, 8.5f), random: rng));

            for (var i = 0; i < 150; i++)
                system.Update(1f / 60f, new Vector3(8f, 5f, 8f), chunks, rng, windTime: t);

            var wind = Wind.Direction(t);
            var along = 0f;
            foreach (var leaf in system.Particles)
                along += (leaf.Position.X - 8.5f) * wind.X + (leaf.Position.Z - 8.5f) * wind.Y;
            Assert.True(along / system.Particles.Count > 0.3f, $"at t={t} the leaves drifted {along} along the wind");
        }
    }

    [Fact]
    public void LandedLeafFadesOutAndIsRemoved()
    {
        var (chunks, _) = Setup();
        var system = new LeafParticleSystem();
        system.AddForTest(new LeafParticle(new Vector3(8.5f, 5.5f, 8.5f), random: new Random(2)));

        for (var i = 0; i < 600 && system.Particles.Count > 0; i++)
            system.Update(1f / 60f, new Vector3(8f, 5f, 8f), chunks, new Random(2));

        Assert.Empty(system.Particles);
    }

    [Fact]
    public void AlphaFadesInAtSpawn()
    {
        var leaf = new LeafParticle(new Vector3(8.5f, 5.5f, 8.5f), random: new Random(3));

        Assert.Equal(0f, leaf.Alpha);
    }

    [Fact]
    public void AmbientSpawnFindsCanopyBottomShell()
    {
        var (chunks, center) = Setup();
        for (var x = 4; x < 12; x++)
        for (var z = 4; z < 12; z++)
            center.SetBlockRaw(x, 3, z, LeavesId, 0);

        var system = new LeafParticleSystem();
        var rng = new Random(42);
        for (var i = 0; i < 1200 && system.Particles.Count == 0; i++)
            system.Update(1f / 60f, new Vector3(8f, 3f, 8f), chunks, rng);

        Assert.NotEmpty(system.Particles);
    }

    [Fact]
    public void BuriedCanopySpawnsNothing()
    {
        var (chunks, center) = Setup();
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 1; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, y, z, LeavesId, 0);

        var system = new LeafParticleSystem();
        var rng = new Random(7);
        for (var i = 0; i < 300; i++)
            system.Update(1f / 60f, new Vector3(8f, 8f, 8f), chunks, rng);

        Assert.Empty(system.Particles);
    }

    [Fact]
    public void BurstRespectsMaxParticles()
    {
        var system = new LeafParticleSystem();
        system.Burst(new Vector3(8f, 3f, 3f), WorldGenerationConfig.LeafParticleMax + 50, new Random(5));
        Assert.Equal(WorldGenerationConfig.LeafParticleMax, system.Particles.Count);
    }

    [Fact]
    public void BurstCellsSamplesAtMostCount()
    {
        var system = new LeafParticleSystem();
        var cells = new List<Vector3>();
        for (var i = 0; i < 100; i++)
            cells.Add(new Vector3(i, 10, 0));

        system.BurstCells(cells, 10, new Random(6));
        Assert.Equal(10, system.Particles.Count);
    }

    /// <summary>
    /// C12(a): eviction at the cap drops the OLDEST leaf and leaves every survivor in spawn
    /// order. BurstRespectsMaxParticles above pins only the COUNT, so it would pass a
    /// swap-remove that quietly reordered the sequence; this fact is what makes the
    /// ParticleRing swap safe to trust. The leaf renderer depth-sorts every frame and so does
    /// not care about source order, but the ring is shared with BlockDebrisSystem, whose order
    /// does reach the GPU (see ParticleRing's doc comment), and the invariant is one invariant.
    ///
    /// One leaf per distinct cell makes each spawn identifiable without touching the particle:
    /// Burst places a leaf at cell + 0.5 + jitter with every jitter component in [-0.5, 0.5),
    /// so the position lies in [cell, cell + 1) and MathF.Floor recovers the spawn index. No
    /// Update is driven here, so nothing moves after it is placed.
    /// </summary>
    [Fact]
    public void EvictionDropsTheOldestLeafAndKeepsTheRestInSpawnOrder()
    {
        const int overflow = 50;
        var max = WorldGenerationConfig.LeafParticleMax;
        var system = new LeafParticleSystem();
        var rng = new Random(11);

        for (var i = 0; i < max + overflow; i++)
            system.Burst(new Vector3(i, 100f, 0f), 1, rng);

        Assert.Equal(max, system.Particles.Count);
        for (var i = 0; i < max; i++)
            Assert.Equal(overflow + i, (int)MathF.Floor(system.Particles[i].Position.X));
    }
}
