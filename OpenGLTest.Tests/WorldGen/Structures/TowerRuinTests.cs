using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Structures;

/// <summary>
/// The ruin rule of a conquered battle tower (<see cref="TowerRuin"/>, BT-8) over the whole box of a few tower shapes: it is a
/// pure function of the cell, nothing unbreakable survives, nothing outside the tower or on its apron is touched, the ruin is
/// far lower than the tower and empties every room above the ground floor's dressing, the ground floor's chests stay while every
/// higher chest goes, and rubble that falls always rests on solid ground.
/// </summary>
[Collection("Worldgen seed")]
public sealed class TowerRuinTests
{
    public TowerRuinTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    public static IEnumerable<object[]> Shapes()
    {
        yield return [WorldGenerationConfig.TowerMinRadius, WorldGenerationConfig.TowerMinFloors, 0];
        yield return [WorldGenerationConfig.TowerMaxRadius, WorldGenerationConfig.TowerMaxFloors, 3];
        yield return [(WorldGenerationConfig.TowerMinRadius + WorldGenerationConfig.TowerMaxRadius) / 2, 6, 1];
    }

    // Every cell of a site's box: the ruin result and the tower's own cell
    private sealed class Ruin
    {
        public required TowerSite Site;
        public required int[] Result;
        public required int[] Original;

        public int Index(int x, int y, int z) =>
            ((y - Site.BottomY) * (Site.MaxZ - Site.MinZ + 1) + (z - Site.MinZ)) * (Site.MaxX - Site.MinX + 1) + (x - Site.MinX);

        public IEnumerable<(int X, int Y, int Z)> Cells()
        {
            for (var y = Site.BottomY; y <= Site.TopY; y++)
            for (var z = Site.MinZ; z <= Site.MaxZ; z++)
            for (var x = Site.MinX; x <= Site.MaxX; x++)
                yield return (x, y, z);
        }

        // The block the cell holds after the collapse, or null where the tower writes nothing
        public byte? After(int x, int y, int z)
        {
            var i = Index(x, y, z);
            if (Result[i] >= 0) return (byte)Result[i];
            return Original[i] >= 0 ? (byte)Original[i] : null;
        }
    }

    private static Ruin Evaluate(TowerSite site)
    {
        var size = (site.MaxX - site.MinX + 1) * (site.MaxZ - site.MinZ + 1) * (site.TopY - site.BottomY + 1);
        var ruin = new Ruin { Site = site, Result = new int[size], Original = new int[size] };
        foreach (var (x, y, z) in ruin.Cells())
        {
            var i = ruin.Index(x, y, z);
            ruin.Result[i] = TowerRuin.RuinAt(site, x, y, z, out ruin.Original[i]);
        }
        return ruin;
    }

    private static TowerRing RingOf(TowerSite s, int x, int z)
    {
        var step = TowerShell.StepOf(s, x - s.CenterX, z - s.CenterZ);
        return TowerShell.RingOf(s, x - s.CenterX, z - s.CenterZ, step);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void ItIsPure(int radius, int floors, int turns)
    {
        var site = TowerSiteFixture.SiteOf(radius, floors, turns);
        var ruin = Evaluate(site);
        var cells = ruin.Cells().ToArray();
        new Random(1234).Shuffle(cells);
        foreach (var (x, y, z) in cells)
        {
            var i = ruin.Index(x, y, z);
            Assert.Equal(ruin.Result[i], TowerRuin.RuinAt(site, x, y, z, out var original));
            Assert.Equal(ruin.Original[i], original);
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void NoUnbreakableBlockSurvives(int radius, int floors, int turns)
    {
        var ruin = Evaluate(TowerSiteFixture.SiteOf(radius, floors, turns));
        var unbreakable = 0;
        foreach (var (x, y, z) in ruin.Cells())
        {
            var i = ruin.Index(x, y, z);
            if (ruin.Original[i] < 0 || !BlockType.IsUnbreakable((byte)ruin.Original[i])) continue;
            unbreakable++;
            Assert.True(ruin.Result[i] >= 0, $"unbreakable cell ({x}, {y}, {z}) kept");
            Assert.False(BlockType.IsUnbreakable((byte)ruin.Result[i]));
        }
        Assert.True(unbreakable > 0);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void CellsOutsideTheTowerAndTheApronAreKept(int radius, int floors, int turns)
    {
        var site = TowerSiteFixture.SiteOf(radius, floors, turns);
        var ruin = Evaluate(site);
        foreach (var (x, y, z) in ruin.Cells())
        {
            var i = ruin.Index(x, y, z);
            if (ruin.Original[i] < 0 || RingOf(site, x, z) == TowerRing.Apron)
                Assert.Equal(-1, ruin.Result[i]);
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheRuinIsLowerThanTheTower(int radius, int floors, int turns)
    {
        var site = TowerSiteFixture.SiteOf(radius, floors, turns);
        var ruin = Evaluate(site);
        foreach (var (x, y, z) in ruin.Cells())
        {
            if (ruin.After(x, y, z) is not { } after) continue;
            if (BlockType.IsSolid(after)) Assert.True(y < site.RoofY, $"solid ruin cell ({x}, {y}, {z}) at or over the roof");
            if (y > site.GroundY + 1 && RingOf(site, x, z) == TowerRing.Room)
                Assert.Equal(BlockType.Air, after);
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void FloorZeroDressingStaysAndHigherChestsAreRemoved(int radius, int floors, int turns)
    {
        var site = TowerSiteFixture.SiteOf(radius, floors, turns);
        foreach (var (x, y, z) in TowerGenerator.ChestCells(site, 0))
            Assert.Equal(-1, TowerRuin.RuinAt(site, x, y, z, out _));
        var higher = Enumerable.Range(1, site.Floors - 1).SelectMany(k => TowerGenerator.ChestCells(site, k))
            .Concat(TowerGenerator.AltarChestCells(site)).ToList();
        Assert.NotEmpty(higher);
        foreach (var (x, y, z) in higher)
            Assert.Equal(BlockType.Air, TowerRuin.RuinAt(site, x, y, z, out _));
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void RubbleOnlyRestsOnSolidGround(int radius, int floors, int turns)
    {
        var site = TowerSiteFixture.SiteOf(radius, floors, turns);
        var ruin = Evaluate(site);
        var piles = 0;
        foreach (var (x, y, z) in ruin.Cells())
        {
            var i = ruin.Index(x, y, z);
            if (ruin.Result[i] < 0) continue;
            var block = (byte)ruin.Result[i];
            if (BlockType.IsSolid(block) && ruin.Original[i] >= 0 && !BlockType.IsSolid((byte)ruin.Original[i]))
            {
                Assert.Equal(site.GroundY + 1, y);
                piles++;
            }
            if (!BlockType.IsFalling(block)) continue;
            Assert.True(y > site.BottomY);
            Assert.True(ruin.After(x, y - 1, z) is { } below && BlockType.IsSolid(below),
                $"falling rubble at ({x}, {y}, {z}) over a non-solid cell");
        }
        Assert.True(piles > 0);
    }
}
