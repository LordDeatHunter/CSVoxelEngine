using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Particles;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// A felled tree's thud: <see cref="TopplingTree"/> reports the moment it swings flat exactly once
/// and where its blocks came down, and <see cref="GroundDust.TreeThud"/> kicks dust up only where the
/// trunk has ground under it.
/// </summary>
public sealed class TreeThudTests
{
    public TreeThudTests() => TestContent.EnsureLoaded();

    private static TopplingTree Trunk(int height)
    {
        var log = ContentRegistry.RequireBlock("oak_log").Id;
        var blocks = new List<TopplingTree.FellBlock>();
        for (var y = 0; y < height; y++)
            blocks.Add(new TopplingTree.FellBlock(new Vector3(4, 10 + y, 4), log, WoodState.FullCube));
        return new TopplingTree(blocks, new Vector3(4.5f, 10f, 4.5f), Vector3.UnitX, drops: false);
    }

    [Fact]
    public void TheLandingIsReportedOnceWhenTheSwingReachesFlat()
    {
        var tree = Trunk(5);
        const float dt = 1f / 60f;
        var elapsed = 0f;
        var landings = new List<float>();
        while (!tree.IsDone)
        {
            tree.Update(dt);
            elapsed += dt;
            if (tree.TryTakeLanding())
                landings.Add(elapsed);
        }
        tree.Dispose();

        var landedAt = Assert.Single(landings);
        Assert.True(landedAt >= WorldGenerationConfig.TreeToppleSeconds);
        Assert.True(landedAt < WorldGenerationConfig.TreeToppleSeconds + dt * 1.5f);
    }

    [Fact]
    public void ALandedTrunkLiesFlatAlongTheFallDirection()
    {
        var tree = Trunk(5);
        var landed = new List<TopplingTree.FellBlock>();
        tree.GatherLanded(landed);
        tree.Dispose();

        Assert.Equal(5, landed.Count);
        Assert.Single(landed.Select(b => b.Cell.Y).Distinct());
        Assert.Single(landed.Select(b => b.Cell.Z).Distinct());
        Assert.Equal(5, landed.Select(b => b.Cell.X).Distinct().Count());
        // Toward +X, away from the stump
        Assert.All(landed, b => Assert.True(b.Cell.X >= 4f));
    }

    [Fact]
    public void TheTrunkKicksDustOnlyWhereThereIsGroundUnderIt()
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        var chunk = manager.Chunks[Vector3.Zero];
        // Ground under x 0..7 at y 3, nothing under x 8..15
        for (var x = 0; x < 8; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 3, z, dirt, 0);

        var debris = new BlockDebrisSystem();
        var overGround = Enumerable.Range(1, 5).Select(x => new Vector3(x, 4, 6)).ToList();
        var spawned = GroundDust.TreeThud(overGround, manager, debris, new System.Random(1));
        Assert.True(spawned > 0);
        Assert.Equal(spawned, debris.Debris.Count);

        var overAir = Enumerable.Range(9, 5).Select(x => new Vector3(x, 4, 6)).ToList();
        Assert.Equal(0, GroundDust.TreeThud(overAir, manager, new BlockDebrisSystem()));
    }

    [Fact]
    public void ALongTrunkPuffsAtNoMorePointsThanTheCap()
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        var chunk = manager.Chunks[Vector3.Zero];
        for (var x = 0; x < Chunk.Size; x++)
            chunk.SetBlockRaw(x, 3, 6, dirt, 0);

        var trunk = Enumerable.Range(0, Chunk.Size).Select(x => new Vector3(x, 4, 6)).ToList();
        var spawned = GroundDust.TreeThud(trunk, manager, new BlockDebrisSystem());
        Assert.True(spawned <= WorldGenerationConfig.TreeThudDustPoints * WorldGenerationConfig.TreeThudDustCount);
        Assert.True(spawned > 0);
    }
}
