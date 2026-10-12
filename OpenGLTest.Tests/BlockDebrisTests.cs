using System;
using System.Numerics;
using silkgltriangle.Content;
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

    /// <summary>
    /// C12(a), and the load-bearing one of the four order facts: debris is the ONLY particle
    /// system whose source order reaches the GPU. RenderManager.RenderBlockDebris walks
    /// <see cref="BlockDebrisSystem.Debris"/> with a plain foreach and submits each cube in that
    /// order, with depth writes on and - on any frame where leaves also drew - blending still
    /// enabled at SrcAlpha/OneMinusSrcAlpha, so overlapping semi-transparent cubes composite in
    /// submission order (ParticleRing's doc comment carries the full argument). BurstRespectsMax
    /// above pins only the count and would pass a swap-remove that reordered the sequence.
    ///
    /// One cube per distinct cell makes each spawn identifiable: Burst places a cube at
    /// cell + 0.5 + jitter with every jitter component in [-0.3, 0.3), so the position lies in
    /// [cell + 0.2, cell + 0.8) and MathF.Floor recovers the spawn index. No Update is driven,
    /// so nothing moves after it is placed.
    /// </summary>
    [Fact]
    public void EvictionDropsTheOldestCubeAndKeepsTheRestInSpawnOrder()
    {
        const int overflow = 50;
        var max = WorldGenerationConfig.BlockDebrisMax;
        var system = new BlockDebrisSystem();
        var rng = new Random(12);

        for (var i = 0; i < max + overflow; i++)
            system.Burst(new Vector3(i, 100f, 0f), StoneId, 1, rng);

        Assert.Equal(max, system.Debris.Count);
        for (var i = 0; i < max; i++)
            Assert.Equal(overflow + i, (int)MathF.Floor(system.Debris[i].Position.X));
    }
}
