using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>A felled limb comes to rest on its real underside, whatever its thickness.</summary>
public sealed class FellBodyTests
{
    public FellBodyTests() => TestContent.EnsureLoaded();

    private static FellBody Limb(int thickness, Func<int, int, int, bool> isSolid)
    {
        var log = ContentRegistry.RequireBlock("oak_log").Id;
        var blocks = new List<TopplingTree.FellBlock>();
        for (var i = 0; i < 4; i++)
            blocks.Add(new TopplingTree.FellBlock(new Vector3(4 + i, 20, 4), log,
                WoodState.Encode(thickness, i == 0 ? WoodState.ParentRoot : 2, false)));
        var centers = blocks.Select(b => b.Cell + new Vector3(0.5f)).ToList();
        return new FellBody(centers, centers.Select(_ => 1f).ToList(), FellContacts.Build(blocks),
            new Vector3(4.5f, 20f, 4.5f), Vector3.UnitZ, hinged: false, isSolid: isSolid);
    }

    private static float RestHeightOfCenter(int thickness)
    {
        var body = Limb(thickness, (_, y, _) => y < 10);
        for (var i = 0; i < 60 * 8 && !body.AtRest; i++)
            body.Advance(1f / 60f);
        Assert.True(body.AtRest);
        return body.PointNow(1).Y;
    }

    // A tall column through the limb's tip block (x = 7), like a neighboring trunk in a dense forest
    private static bool GroundAndTrunk(int x, int y, int z) => y < 10 || (x == 7 && z == 4 && y < 40);

    [Fact]
    public void AWallTheLimbOverlapsNeverLiftsIt()
    {
        var body = Limb(15, GroundAndTrunk);
        var startY = body.Center.Y;
        for (var i = 0; i < 60 * 8 && !body.AtRest; i++)
        {
            body.Advance(1f / 60f);
            Assert.True(body.Center.Y <= startY + 0.01f, $"lifted to {body.Center.Y} from {startY}");
        }
    }

    // A full-thickness trunk standing on ground (top y = 10) at x = 4, pinned at its cut and kicked toward +X
    private static FellBody Trunk(Func<int, int, int, bool> isSolid)
    {
        var log = ContentRegistry.RequireBlock("oak_log").Id;
        var blocks = new List<TopplingTree.FellBlock>();
        for (var i = 0; i < 8; i++)
            blocks.Add(new TopplingTree.FellBlock(new Vector3(4, 10 + i, 4), log,
                WoodState.Encode(15, i == 0 ? WoodState.ParentRoot : 4, false)));
        var centers = blocks.Select(b => b.Cell + new Vector3(0.5f)).ToList();
        return new FellBody(centers, centers.Select(_ => 1f).ToList(), FellContacts.Build(blocks),
            new Vector3(4.5f, 10f, 4.5f), -Vector3.UnitZ, hinged: true, isSolid: isSolid);
    }

    [Theory]
    // The hillside beside the trunk's base is a block higher than the cut
    [InlineData(5, 10)]
    // A neighbor's branch grows right beside the trunk, up in the crown
    [InlineData(5, 15)]
    public void WhatTheStandingTreeAlreadyTouchesNeverLiftsIt(int x, int y)
    {
        var body = Trunk((cx, cy, cz) => cy < 10 || (cx == x && cy == y && cz == 4));
        var startY = body.Center.Y;
        for (var i = 0; i < 60 * 8 && !body.AtRest; i++)
        {
            body.Advance(1f / 60f);
            Assert.True(body.Center.Y <= startY + 0.01f, $"lifted to {body.Center.Y} from {startY} after {i} frames");
        }
    }

    [Fact]
    public void ABranchSplitOffAPinnedTreeDropsFreeWhileTheRestKeepsThePin()
    {
        var trunk = Trunk((_, y, _) => y < 10);
        trunk.Advance(1f / 60f);
        Assert.True(trunk.Hinged);

        var centers = new[] { new Vector3(4.5f, 16.5f, 4.5f), new Vector3(4.5f, 17.5f, 4.5f) };
        var points = centers.Select(c => new[] { c - new Vector3(0f, 0.5f, 0f) }).ToList();
        FellBody Split(bool hinged) => new(centers, [1f, 1f], points, new Vector3(4.5f, 10f, 4.5f), -Vector3.UnitZ,
            hinged, (_, y, _) => y < 10, trunk);

        Assert.False(Split(hinged: false).Hinged);
        Assert.True(Split(hinged: true).Hinged);
    }

    [Fact]
    public void SinkingIntoTheGroundIsEasedOutNoFasterThanTheCap()
    {
        // A free limb dropped with its underside nearly a block into the ground
        var log = ContentRegistry.RequireBlock("oak_log").Id;
        var blocks = new List<TopplingTree.FellBlock>();
        for (var i = 0; i < 4; i++)
            blocks.Add(new TopplingTree.FellBlock(new Vector3(4 + i, 9, 4), log,
                WoodState.Encode(15, i == 0 ? WoodState.ParentRoot : 2, false)));
        var centers = blocks.Select(b => b.Cell + new Vector3(0.5f)).ToList();
        var body = new FellBody(centers, centers.Select(_ => 1f).ToList(), FellContacts.Build(blocks),
            new Vector3(4.5f, 9f, 4.5f), Vector3.UnitZ, hinged: false, isSolid: (_, y, _) => y < 10);
        var startY = body.Center.Y;

        const float frame = 1f / 60f;
        body.Advance(frame);
        Assert.InRange(body.Center.Y - startY, float.NegativeInfinity, SimulationConfig.TreeFellDepenetrateSpeed * frame + 0.01f);
    }

    [Fact]
    public void ABlockRunningFastThroughAWallIsAHardImpact()
    {
        var body = Limb(15, GroundAndTrunk);
        var struck = false;
        for (var i = 0; i < 60 && !struck; i++)
        {
            body.Advance(1f / 60f);
            struck = body.HardImpacts.Contains(3) && body.PointNow(3).Y > 11f;
        }
        Assert.True(struck);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(15)]
    public void ALimbSettlesWithItsOwnUndersideOnTheGround(int thickness)
    {
        var half = (thickness + 1) / 32f;
        // The block's center sits half its diameter above the ground (top face y = 10), within the contact skin
        Assert.Equal(10f + half, RestHeightOfCenter(thickness), 0.08f);
    }
}
