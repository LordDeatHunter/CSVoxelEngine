using System;
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

    // Flat ground whose top face is y = 10
    private const int GroundTop = 10;
    private static bool Ground(int x, int y, int z) => y < GroundTop;

    private static TopplingTree Trunk(int height)
    {
        var log = ContentRegistry.RequireBlock("oak_log").Id;
        var blocks = new List<TopplingTree.FellBlock>();
        for (var y = 0; y < height; y++)
            blocks.Add(new TopplingTree.FellBlock(new Vector3(4, GroundTop + y, 4), log, WoodState.FullCube));
        return new TopplingTree(blocks, new Vector3(4.5f, GroundTop, 4.5f), Vector3.UnitX, drops: false, isSolid: Ground);
    }

    private static TopplingTree Limb(int length, int height)
    {
        var log = ContentRegistry.RequireBlock("oak_log").Id;
        var blocks = new List<TopplingTree.FellBlock>();
        for (var i = 0; i < length; i++)
            blocks.Add(new TopplingTree.FellBlock(new Vector3(4 + i, height, 4), log, WoodState.FullCube));
        return new TopplingTree(blocks, new Vector3(4.5f, height, 4.5f), Vector3.UnitX, drops: false, isSolid: Ground);
    }

    private const float Dt = 1f / 60f;

    /// <summary>Runs the tree until it is done, returning the seconds each landing was reported at.</summary>
    private static List<float> RunToRest(TopplingTree tree)
    {
        var elapsed = 0f;
        var landings = new List<float>();
        while (!tree.IsDone && elapsed < 40f)
        {
            tree.Update(Dt);
            elapsed += Dt;
            if (tree.TryTakeLanding())
                landings.Add(elapsed);
        }
        return landings;
    }

    [Fact]
    public void TheLandingIsReportedOnceAndBeforeTheTreeComesToRest()
    {
        var tree = Trunk(5);
        var landings = RunToRest(tree);
        var done = tree.IsDone;
        tree.Dispose();

        Assert.True(done);
        var landedAt = Assert.Single(landings);
        Assert.True(landedAt > 0.2f, "a standing tree does not land instantly");
        Assert.True(landedAt < SimulationConfig.TreeFellMaxSimSeconds);
    }

    [Fact]
    public void AFelledTrunkTipsOverItsStumpAndLiesOnTheGroundAwayFromTheFeller()
    {
        var tree = Trunk(5);
        // While it still tips, the cut stays pinned to the stump
        for (var i = 0; i < 12; i++)
            tree.Update(Dt);
        Assert.True(Vector3.Distance(Vector3.Transform(Vector3.Zero, tree.Parts[0].Model), new Vector3(4.5f, GroundTop, 4.5f)) < 0.05f);

        RunToRest(tree);
        var landed = new List<TopplingTree.FellBlock>();
        tree.GatherLanded(landed);
        tree.Dispose();

        Assert.Equal(5, landed.Count);
        // On the ground, spread along the fall direction (+X), not stacked or hovering
        Assert.All(landed, b => Assert.InRange(b.Cell.Y, GroundTop, GroundTop + 1));
        Assert.True(landed.Select(b => b.Cell.X).Distinct().Count() >= 4);
        Assert.True(landed.Average(b => b.Cell.X) > 5f);
    }

    [Fact]
    public void AWholeTreeWithACanopySettlesOnTheGroundBeforeTheTimeCap()
    {
        var log = ContentRegistry.RequireBlock("oak_log").Id;
        var leaf = ContentRegistry.RequireBlock("oak_leaves").Id;
        var blocks = new List<TopplingTree.FellBlock>();
        for (var y = 0; y < 12; y++)
            blocks.Add(new TopplingTree.FellBlock(new Vector3(4, GroundTop + y, 4), log, WoodState.FullCube));
        for (var x = 2; x <= 6; x++)
        for (var y = 8; y <= 13; y++)
        for (var z = 2; z <= 6; z++)
            if (x != 4 || z != 4)
                blocks.Add(new TopplingTree.FellBlock(new Vector3(x, GroundTop + y, z), leaf, 0));
        var tree = new TopplingTree(blocks, new Vector3(4.5f, GroundTop, 4.5f), Vector3.UnitX, drops: false,
            isSolid: Ground);

        RunToRest(tree);
        var landed = new List<TopplingTree.FellBlock>();
        tree.GatherLanded(landed);
        var done = tree.IsDone;
        tree.Dispose();

        Assert.True(done);
        // The crown holds the trunk up on its canopy, and nothing sank into the ground
        Assert.All(landed, b => Assert.InRange(b.Cell.Y, GroundTop, GroundTop + 6));
    }

    [Fact]
    public void ASideBranchThatHitsTheGroundSnapsOffAndFallsOnItsOwn()
    {
        var log = ContentRegistry.RequireBlock("oak_log").Id;
        var blocks = new List<TopplingTree.FellBlock>();
        // Wood links by parent edges: the trunk's parents point down (the base is the root), the
        // branch's point back toward the trunk
        for (var y = 0; y < 9; y++)
            blocks.Add(new TopplingTree.FellBlock(new Vector3(4, GroundTop + y, 4), log,
                WoodState.Encode(WoodState.FullCube, y == 0 ? WoodState.ParentRoot : 5, false)));
        // A branch leaving the trunk toward the fall direction: its tip is what the ground meets first
        for (var x = 5; x <= 8; x++)
            blocks.Add(new TopplingTree.FellBlock(new Vector3(x, GroundTop + 6, 4), log,
                WoodState.Encode(WoodState.FullCube, 2, false)));
        var tree = new TopplingTree(blocks, new Vector3(4.5f, GroundTop, 4.5f), Vector3.UnitX, drops: false,
            isSolid: Ground);

        RunToRest(tree);
        var parts = tree.Parts.Count;
        var landed = new List<TopplingTree.FellBlock>();
        tree.GatherLanded(landed);
        tree.Dispose();

        Assert.True(parts > 1, "the branch stays welded to the trunk");
        Assert.Equal(blocks.Count, landed.Count);
        Assert.All(landed, b => Assert.InRange(b.Cell.Y, GroundTop, GroundTop + 3));
    }

    [Fact]
    public void ALimbCutOffInMidAirFallsToTheGroundInsteadOfHoveringWhereItWasCut()
    {
        const int height = 16;
        var limb = Limb(3, height);
        for (var i = 0; i < 15; i++)
            limb.Update(Dt);
        var mid = Vector3.Transform(Vector3.Zero, limb.Parts[0].Model).Y;
        Assert.InRange(mid, GroundTop, height - 0.5f);

        RunToRest(limb);
        var landed = new List<TopplingTree.FellBlock>();
        limb.GatherLanded(landed);
        limb.Dispose();
        Assert.All(landed, b => Assert.InRange(b.Cell.Y, GroundTop, GroundTop + 1));
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
        Assert.True(spawned <= JuiceConfig.TreeThudDustPoints * JuiceConfig.TreeThudDustCount);
        Assert.True(spawned > 0);
    }
}
