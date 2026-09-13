using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class LeafParticleTests
{
    public LeafParticleTests() => TestContent.EnsureLoaded();

    private static byte LeavesId => ContentRegistry.RequireBlock("leaves").Id;
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
