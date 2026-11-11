using System;
using System.Collections.Generic;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Structures;

/// <summary>
/// The nearest-structure search behind <c>/locate</c> (<see cref="StructureLocator"/>): it reports exactly the nearest tower of
/// every cell it scans, a ring cap of zero looks only in the query's own cell, and the teleport lands on the generated ground
/// beside its tower, outside the footprint and inside the tower's own cell and site ring.
/// </summary>
[Collection("Worldgen seed")]
public sealed class StructureLocatorTests
{
    private const int Rings = 2;
    private const int Cell = WorldGenerationConfig.TowerCellBlocks;

    private static readonly (int X, int Z)[] Queries = [(0, 0), (4100, -2700), (-3300, 5200)];

    public StructureLocatorTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    private static double DistanceTo(TowerSite site, int x, int z)
    {
        var dx = (double)site.CenterX - x;
        var dz = (double)site.CenterZ - z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Every location the locator reports for the queries within <see cref="Rings"/>.</summary>
    private static List<StructureLocation> FoundInTheWindow()
    {
        var found = new List<StructureLocation>();
        foreach (var (x, z) in Queries)
            if (StructureLocator.TryFindTower(x, z, Rings, out var location))
                found.Add(location);
        return found;
    }

    [Fact]
    public void TheLocatorFindsTheNearestTowerOfEveryCellItScans()
    {
        var anyFound = false;
        foreach (var (x, z) in Queries)
        {
            TowerSite? best = null;
            var bestDistance = double.MaxValue;
            var homeX = TowerPlacement.CellOf(x);
            var homeZ = TowerPlacement.CellOf(z);
            for (var cellX = homeX - Rings; cellX <= homeX + Rings; cellX++)
            for (var cellZ = homeZ - Rings; cellZ <= homeZ + Rings; cellZ++)
            {
                if (!TowerPlacement.TryGetSite(cellX, cellZ, out var site)) continue;
                var d = DistanceTo(site, x, z);
                if (d >= bestDistance) continue;
                bestDistance = d;
                best = site;
            }

            var found = StructureLocator.TryFindTower(x, z, Rings, out var location);
            Assert.Equal(best != null, found);
            if (best == null) continue;
            anyFound = true;
            Assert.Equal(bestDistance, location.Distance, 1e-9);
            Assert.Equal((best.CenterX, best.CenterZ), (location.X, location.Z));
        }
        Assert.True(anyFound, "no query found a tower, so nothing was compared");
    }

    [Fact]
    public void ARingCapOfZeroOnlyLooksInTheQuerysOwnCell()
    {
        for (var cellX = -2; cellX <= 2; cellX++)
        for (var cellZ = -2; cellZ <= 2; cellZ++)
        {
            var x = cellX * Cell + Cell / 2;
            var z = cellZ * Cell + Cell / 2;
            var expected = TowerPlacement.TryGetSite(cellX, cellZ, out var site);

            Assert.Equal(expected, StructureLocator.TryFindTower(x, z, 0, out var location));
            if (expected)
                Assert.Equal((site.CenterX, site.CenterZ), (location.X, location.Z));
        }
    }

    [Fact]
    public void TheLandingStandsOnTheGroundBesideItsTower()
    {
        var found = FoundInTheWindow();
        Assert.NotEmpty(found);
        foreach (var location in found)
        {
            Assert.True(TowerPlacement.TryFindAt(location.X, location.Z, out var site));

            var insideFootprint = location.LandX >= site.MinX && location.LandX <= site.MaxX
                                  && location.LandZ >= site.MinZ && location.LandZ <= site.MaxZ;
            Assert.False(insideFootprint, "the landing lies inside the tower's footprint");
            Assert.Equal((site.CellX, site.CellZ),
                (TowerPlacement.CellOf(location.LandX), TowerPlacement.CellOf(location.LandZ)));
            Assert.Equal(RuinPlacement.HeightAt(location.LandX, location.LandZ) + 1, location.LandFeetY);

            var dx = (double)location.LandX - location.X;
            var dz = (double)location.LandZ - location.Z;
            Assert.True(Math.Sqrt(dx * dx + dz * dz) <= TowerPlacement.Reach + WorldGenerationConfig.TowerSiteWaterRing,
                "the landing lies outside the ring the site rules judged");
        }
    }
}
