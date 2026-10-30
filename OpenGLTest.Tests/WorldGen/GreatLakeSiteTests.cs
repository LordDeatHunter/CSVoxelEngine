using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// The great-lake site verdicts (<see cref="GreatLakes.BuildSite"/>) and the per-column sample
/// (<see cref="GreatLakes.Sample(float, float, out int, out int, out float, out (int X, int Z))"/>): a
/// site's water sits under its rim and over its floor, a river-fed lake rests at the sea level, no site
/// reaches the spawn clearing, accepted sites never overlap, a lake stands wholly on lake-holding land
/// and clear of every massif and lone peak, an island stands above its lake, the rim walk covers the
/// dry shore band, and the sample is exactly zero outside every accepted footprint and near the spawn.
/// </summary>
public sealed class GreatLakeSiteTests
{
    private readonly ITestOutputHelper _output;

    public GreatLakeSiteTests(ITestOutputHelper output)
    {
        TestContent.EnsureLoaded();
        _output = output;
    }

    private const int ScanHalfExtent = 16000;
    private const int ScanStride = 97;
    private const int OriginPatchHalfExtent = 60;
    private const int RimRingPoints = 64;
    private const int HalfRadiusRingPoints = 16;

    private const float Guard = WorldGenerationConfig.GreatLakeRavineGuardBlocks;

    // The farthest a lake can reach from its cell's rolled center, guard included: cells this far past
    // the scan box still reach into it
    private const float CellMargin = WorldGenerationConfig.GreatLakeMaxRadius * GreatLakes.ReachFactor + Guard;

    internal readonly record struct Site((int X, int Z) Cell, GreatLakeSite Value);

    private static readonly Lazy<(Site[] Sites, int Rolled)> Scan = new(BuildSites);

    /// <summary>Every accepted great lake reaching into the ±16000 scan box, shared with
    /// <see cref="GreatLakeTests"/> so the box is scanned once per run.</summary>
    internal static Site[] AcceptedSites => Scan.Value.Sites;

    private static (Site[] Sites, int Rolled) BuildSites()
    {
        const int cellBlocks = WorldGenerationConfig.GreatLakeCellBlocks;
        var min = MathUtils.FloorToInt((-ScanHalfExtent - CellMargin) / cellBlocks);
        var max = MathUtils.FloorToInt((ScanHalfExtent + CellMargin) / cellBlocks);
        var sites = new List<Site>();
        var rolled = 0;
        for (var gx = min; gx <= max; gx++)
        for (var gz = min; gz <= max; gz++)
        {
            // Replays the cell roll ("GLAK" salt) only to count the candidates, for the log
            if (TerrainSites.TryRoll(gx, gz, cellBlocks, 0x474C414B, WorldGenerationConfig.GreatLakeChance,
                    WorldGenerationConfig.GreatLakeRadiusSkew, out _, out _, out _, out _))
                rolled++;

            var site = GreatLakes.SiteAt(gx, gz);
            if (site.Exists) sites.Add(new Site((gx, gz), site));
        }

        return (sites.ToArray(), rolled);
    }

    private Site[] Sites
    {
        get
        {
            var (sites, rolled) = Scan.Value;
            _output.WriteLine($"{sites.Length} great lakes accepted of {rolled} rolled candidates, "
                              + $"{sites.Count(s => s.Value.RiverFed)} river-fed, "
                              + $"{sites.Count(s => s.Value.Island.Radius > 0f)} with an island");
            Assert.NotEmpty(sites);
            return sites;
        }
    }

    private static IEnumerable<GreatLakeLobe> Lobes(GreatLakeSite site)
    {
        for (var k = 0; k < site.LobeCount; k++)
            yield return site.Lobe(k);
    }

    private static float Land(float x, float z) => TerrainGenerator.LandHeight(x, z, out _, out _, out _, out _);

    private static float Distance(float x, float z, float centerX, float centerZ)
    {
        var dx = x - centerX;
        var dz = z - centerZ;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static IEnumerable<(float X, float Z)> Ring(float centerX, float centerZ, float radius, int points)
    {
        for (var k = 0; k < points; k++)
        {
            var angle = k * (MathF.Tau / points);
            yield return (centerX + MathF.Cos(angle) * radius, centerZ + MathF.Sin(angle) * radius);
        }
    }

    [Fact]
    public void ASitesWaterSitsUnderItsRimAndOverItsFloor()
    {
        var sites = Sites;
        foreach (var (cell, site) in sites)
        {
            Assert.True(site.Floor < site.Surface, $"the great lake in cell {cell} has its floor at or above its surface");
            if (site.RiverFed)
                Assert.True(site.Surface == WorldGenerationConfig.SeaLevel,
                    $"the river-fed great lake in cell {cell} rests at {site.Surface}, not the river's sea level");

            foreach (var lobe in Lobes(site))
            foreach (var (x, z) in Ring(lobe.X, lobe.Z, lobe.Radius + 1f, RimRingPoints))
                Assert.True(Land(x, z) >= site.Surface,
                    $"the rim of the great lake in cell {cell} dips to {Land(x, z)} at ({x}, {z}), under its surface {site.Surface}");
        }

        Assert.Contains(sites, s => s.Value.RiverFed);
    }

    [Fact]
    public void NoSiteReachesTheSpawnClearing()
    {
        foreach (var (cell, site) in Sites)
        foreach (var lobe in Lobes(site))
            Assert.True(Distance(lobe.X, lobe.Z, 0f, 0f) - lobe.Radius - Guard >= WorldGenerationConfig.GreatLakeSpawnClearRadius,
                $"a lobe of the great lake in cell {cell} reaches into the spawn clearing");
    }

    [Fact]
    public void AcceptedSitesNeverOverlap()
    {
        var sites = Sites;
        Assert.True(sites.Length >= 2, "fewer than two great lakes to compare");
        for (var i = 0; i < sites.Length; i++)
        for (var j = i + 1; j < sites.Length; j++)
        foreach (var a in Lobes(sites[i].Value))
        foreach (var b in Lobes(sites[j].Value))
            Assert.True(Distance(a.X, a.Z, b.X, b.Z) >= a.Radius + b.Radius + 2f * Guard,
                $"the great lakes in cells {sites[i].Cell} and {sites[j].Cell} overlap");
    }

    [Fact]
    public void ALakeStandsWhollyOnLakeGround()
    {
        foreach (var (cell, site) in Sites)
        foreach (var lobe in Lobes(site))
        foreach (var (x, z) in Ring(lobe.X, lobe.Z, lobe.Radius * 0.5f, HalfRadiusRingPoints).Append((lobe.X, lobe.Z)))
        {
            var biome = TerrainGenerator.BiomeWeights(x, z, out var ocean, out var desert);
            Assert.True(ocean == 0f, $"the great lake in cell {cell} has ocean weight {ocean} at ({x}, {z})");
            Assert.True(WaterBodies.SurfaceWaterGate(desert) >= 1f,
                $"the great lake in cell {cell} reaches toward a desert at ({x}, {z})");
            Assert.True(WaterBodies.CanHoldSurfaceWater(biome),
                $"the great lake in cell {cell} stands on {biome}, which holds no surface water, at ({x}, {z})");
        }
    }

    [Fact]
    public void NoMassifOrLonePeakStandsInAGreatLake()
    {
        var checkedSites = 0;
        foreach (var (cell, site) in Sites)
        {
            checkedSites += AssertClearOf(cell, site, WorldGenerationConfig.MassifCellBlocks,
                WorldGenerationConfig.MassifMaxRadius, TerrainPeaks.MassifSiteAt, "massif");
            checkedSites += AssertClearOf(cell, site, WorldGenerationConfig.PeakCellBlocks,
                WorldGenerationConfig.PeakMaxRadius, TerrainPeaks.PeakSiteAt, "lone peak");
        }

        _output.WriteLine($"{checkedSites} massifs and lone peaks checked near great lakes");
        Assert.True(checkedSites > 0, "no massif or lone peak stood near any great lake to check");
    }

    // Every accepted relief site whose cell lies within a cell of reaching the lake: none may meet a lobe
    // disc grown by the guard. Returns how many relief sites were checked.
    private static int AssertClearOf((int X, int Z) cell, GreatLakeSite site, int cellBlocks, float maxRadius,
        Func<int, int, TerrainSite> siteAt, string kind)
    {
        var reach = site.Reach + Guard + maxRadius + cellBlocks;
        TerrainSites.CellRange(site.Lobe0.X, cellBlocks, reach, out var minX, out var maxX);
        TerrainSites.CellRange(site.Lobe0.Z, cellBlocks, reach, out var minZ, out var maxZ);
        var count = 0;
        for (var gx = minX; gx <= maxX; gx++)
        for (var gz = minZ; gz <= maxZ; gz++)
        {
            var relief = siteAt(gx, gz);
            if (!relief.Exists) continue;

            count++;
            foreach (var lobe in Lobes(site))
                Assert.True(Distance(relief.CenterX, relief.CenterZ, lobe.X, lobe.Z) >= relief.Radius + lobe.Radius + Guard,
                    $"the {kind} in cell ({gx}, {gz}) stands in the great lake of cell {cell}");
        }

        return count;
    }

    [Fact]
    public void AnIslandStandsAboveItsLake()
    {
        var islands = Sites.Where(s => s.Value.Island.Radius > 0f).ToArray();
        Assert.NotEmpty(islands);
        foreach (var (cell, site) in islands)
        {
            var island = site.Island;
            Assert.True(Land(island.X, island.Z) >= site.Surface + WorldGenerationConfig.GreatLakeBankDepth,
                $"the island of the great lake in cell {cell} stands at {Land(island.X, island.Z)}, "
                + $"under a bank over the surface {site.Surface}");
            Assert.True(Distance(island.X, island.Z, site.Lobe0.X, site.Lobe0.Z) + island.Radius < site.Lobe0.Radius,
                $"the island of the great lake in cell {cell} leaves its main lobe");
        }
    }

    [Fact]
    public void TheRimWalkCoversTheDryShoreBand()
    {
        // At the rim walk's inner edge, a RimBandShare of the shore ramp in from the outline, the bowl
        // weight is already at the threshold: everything the walk leaves out is under standing water
        Assert.True(MathUtils.SmoothStep(WorldGenerationConfig.GreatLakeRimBandShare) >= WorldGenerationConfig.WaterBodyThreshold,
            "the rim walk stops short of where the bowl weight reaches the water threshold");

        // The per-column cull at the nominal radius times ReachFactor is exact only while no lobe edge
        // lies farther out
        foreach (var (cell, site) in Sites)
        {
            Assert.True(site.Reach <= site.Lobe0.Radius * GreatLakes.ReachFactor,
                $"the great lake in cell {cell} reaches past the per-column cull");
            foreach (var lobe in Lobes(site))
                Assert.True(Distance(lobe.X, lobe.Z, site.Lobe0.X, site.Lobe0.Z) + lobe.Radius <= site.Reach + 0.01f,
                    $"a lobe of the great lake in cell {cell} reaches past the site's reach");
        }
    }

    [Fact]
    public void TheSampleIsZeroOutsideEveryFootprintAndNearTheSpawn()
    {
        var sites = Sites;
        var points = new List<(float X, float Z)>();
        for (var x = -ScanHalfExtent; x <= ScanHalfExtent; x += ScanStride)
        for (var z = -ScanHalfExtent; z <= ScanHalfExtent; z += ScanStride)
            points.Add((x, z));
        for (var x = -OriginPatchHalfExtent; x <= OriginPatchHalfExtent; x++)
        for (var z = -OriginPatchHalfExtent; z <= OriginPatchHalfExtent; z++)
            points.Add((x, z));

        const float clear = WorldGenerationConfig.GreatLakeSpawnClearRadius;
        var sawWater = false;
        var sawGuardOnly = false;
        foreach (var (x, z) in points)
        {
            var weight = GreatLakes.Sample(x, z, out var surface, out _, out var guard, out var cell);
            if (weight > 0f) sawWater = true;
            else if (guard > 0f) sawGuardOnly = true;

            if (Distance(x, z, 0f, 0f) < clear)
            {
                Assert.True(weight == 0f && guard == 0f, $"a great lake reaches ({x}, {z}) inside the spawn clearing");
                continue;
            }

            var inFootprint = false;
            var inLobe = false;
            foreach (var (_, site) in sites)
            foreach (var lobe in Lobes(site))
            {
                var d = Distance(x, z, lobe.X, lobe.Z);
                if (d < lobe.Radius + Guard) inFootprint = true;
                if (d < lobe.Radius) inLobe = true;
            }

            if (!inFootprint)
                Assert.True(BitConverter.SingleToInt32Bits(weight) == 0 && BitConverter.SingleToInt32Bits(guard) == 0,
                    $"the great-lake sample at ({x}, {z}) is {weight} (guard {guard}) outside every footprint");
            if (weight <= 0f) continue;

            Assert.True(inLobe, $"the great-lake sample at ({x}, {z}) has weight {weight} outside every lobe");
            var owner = sites.Where(s => s.Cell == cell).ToArray();
            Assert.True(owner.Length == 1, $"the great-lake sample at ({x}, {z}) names cell {cell}, which holds no accepted lake");
            Assert.True(owner[0].Value.Surface == surface,
                $"the great-lake sample at ({x}, {z}) reports surface {surface}, not its lake's {owner[0].Value.Surface}");
        }

        Assert.True(sawWater, "no scanned point fell inside a great lake");
        Assert.True(sawGuardOnly, "no scanned point fell in a great lake's guard band alone");
    }
}
