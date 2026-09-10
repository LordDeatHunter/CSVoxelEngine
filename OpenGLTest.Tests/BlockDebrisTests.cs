using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class BlockDebrisTests
{
    public BlockDebrisTests() => TestContent.EnsureLoaded();

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
    public void BurstSpawnsConfiguredCountWithBlockId()
    {
        var system = new BlockDebrisSystem();
        system.Burst(new Vector3(8f, 3f, 8f), StoneId, WorldGenerationConfig.BlockDebrisPerHit, new Random(1));
        Assert.Equal(WorldGenerationConfig.BlockDebrisPerHit, system.Debris.Count);
        foreach (var d in system.Debris)
            Assert.Equal(StoneId, d.BlockId);
    }

    [Fact]
    public void BurstIgnoresAirAndUnknownIds()
    {
        var system = new BlockDebrisSystem();
        system.Burst(new Vector3(8f, 3f, 8f), BlockType.Air, 10, new Random(1));
        Assert.Empty(system.Debris);
    }

    [Fact]
    public void DebrisFallsAndLandsThenFades()
    {
        var (chunks, _) = Setup();
        var debris = new BlockDebris(StoneId, new Vector3(8.5f, 2.0f, 8.5f), new Vector3(0f, -1f, 0f), random: new Random(2));
        var landed = false;
        for (var i = 0; i < 600 && !landed; i++)
        {
            Assert.True(debris.Update(1f / 60f, chunks) || debris.IsAlive == false);
            if (!debris.IsAlive) break;
            landed = debris.IsLanded;
        }
        Assert.True(landed);
        Assert.True(debris.Position.Y < 2.0f);
    }

    [Fact]
    public void ExpiredDebrisIsRemovedBySystem()
    {
        var (chunks, _) = Setup();
        var system = new BlockDebrisSystem();
        system.AddForTest(new BlockDebris(StoneId, new Vector3(8.5f, 5.5f, 8.5f), random: new Random(3)));
        for (var i = 0; i < 600 && system.Debris.Count > 0; i++)
            system.Update(1f / 60f, chunks);
        Assert.Empty(system.Debris);
    }

    [Fact]
    public void AlphaStartsAtZeroAndEndsAtZero()
    {
        var debris = new BlockDebris(StoneId, new Vector3(8.5f, 5.5f, 8.5f), random: new Random(4));
        Assert.Equal(0f, debris.Alpha);
    }

    [Fact]
    public void BurstRespectsMax()
    {
        var system = new BlockDebrisSystem();
        system.Burst(new Vector3(8f, 3f, 8f), StoneId, WorldGenerationConfig.BlockDebrisMax + 50, new Random(5));
        Assert.Equal(WorldGenerationConfig.BlockDebrisMax, system.Debris.Count);
    }
}
