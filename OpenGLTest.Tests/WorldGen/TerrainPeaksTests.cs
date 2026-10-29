using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// The hashed peaks (<see cref="TerrainPeaks"/>): land outside every massif is bit-identical to the
/// relief land, a site never lowers land, the soft ceiling is the identity below its knee and stays
/// under the ceiling, a massif's crown stands above its rim and is a peak rather than a plateau,
/// every massif term comes from an accepted site, accepted sites keep their spacing and no summit
/// reaches the cloud layer. A lone peak only ever stands on land that passes its flatness test,
/// never on a massif, land outside every lone peak is bit-identical to the land without them, and
/// lone peaks keep their spacing too.
/// </summary>
public sealed class TerrainPeaksTests
{
    private readonly ITestOutputHelper _output;

    public TerrainPeaksTests(ITestOutputHelper output)
    {
        TestContent.EnsureLoaded();
        _output = output;
    }

    private const int ScanHalfExtent = 16000;
    private const int ScanStride = 97;
    private const int OriginPatchHalfExtent = 60;

    private readonly record struct Site((int X, int Z) Cell, TerrainSite Value);

    private readonly record struct Sample(float X, float Z, float Massif, (int X, int Z) Cell,
        float LonePeak, (int X, int Z) PeakCell);

    private static readonly Lazy<Site[]> Sites = new(() => BuildSites(WorldGenerationConfig.MassifCellBlocks,
        TerrainPeaks.MassifSiteAt));
    private static readonly Lazy<Site[]> PeakSites = new(() => BuildSites(WorldGenerationConfig.PeakCellBlocks,
        TerrainPeaks.PeakSiteAt));
    private static readonly Lazy<Sample[]> Scan = new(BuildScan);

    private static Site[] BuildSites(float cell, Func<int, int, TerrainSite> siteAt)
    {
        var min = MathUtils.FloorToInt(-ScanHalfExtent / cell);
        var max = MathUtils.FloorToInt(ScanHalfExtent / cell);
        var sites = new List<Site>();
        for (var gx = min; gx <= max; gx++)
        for (var gz = min; gz <= max; gz++)
        {
            var site = siteAt(gx, gz);
            if (site.Exists) sites.Add(new Site((gx, gz), site));
        }

        return sites.ToArray();
    }

    private static Sample[] BuildScan()
    {
        var samples = new List<Sample>();
        for (var x = -ScanHalfExtent; x <= ScanHalfExtent; x += ScanStride)
        for (var z = -ScanHalfExtent; z <= ScanHalfExtent; z += ScanStride)
            samples.Add(SampleAt(x, z));
        for (var x = -OriginPatchHalfExtent; x <= OriginPatchHalfExtent; x++)
        for (var z = -OriginPatchHalfExtent; z <= OriginPatchHalfExtent; z++)
            samples.Add(SampleAt(x, z));
        return samples.ToArray();
    }

    private static Sample SampleAt(float x, float z)
    {
        var massif = TerrainPeaks.MassifHeight(x, z, out var cell);
        var lonePeak = TerrainPeaks.LonePeakHeight(x, z, out var peakCell);
        return new Sample(x, z, massif, cell, lonePeak, peakCell);
    }

    private static float Land(float x, float z) => TerrainGenerator.LandHeight(x, z, out _, out _, out _, out _);

    // The land without lone peaks: a massif's own shape, whatever lone peak stands beside it
    private static float PreFeature(float x, float z) => TerrainGenerator.PreFeatureLandHeight(x, z);

    private static float DistanceSquared(float x, float z, TerrainSite site)
    {
        var dx = x - site.CenterX;
        var dz = z - site.CenterZ;
        return dx * dx + dz * dz;
    }

    private static bool BitEqual(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    // The highest land over a 4-block grid within a quarter of the site's radius of its center.
    private static float CrownMax(TerrainSite site, Func<float, float, float> heightAt)
    {
        var reach = site.Radius / 4f;
        var max = float.MinValue;
        for (var dx = -reach; dx <= reach; dx += 4f)
        for (var dz = -reach; dz <= reach; dz += 4f)
        {
            if (dx * dx + dz * dz > reach * reach) continue;
            max = MathF.Max(max, heightAt(site.CenterX + dx, site.CenterZ + dz));
        }

        return max;
    }

    // The land at n points evenly around the circle of the given radius about the site's center.
    private static IEnumerable<float> RingHeights(TerrainSite site, float radius, int n, Func<float, float, float> heightAt)
    {
        for (var k = 0; k < n; k++)
        {
            var angle = MathF.Tau * k / n;
            yield return heightAt(site.CenterX + radius * MathF.Cos(angle), site.CenterZ + radius * MathF.Sin(angle));
        }
    }

    // Asserts that one land profile and its outs are bit-equal to another's at (x, z).
    private static void AssertProfilesBitEqual(float x, float z, string what,
        float land, Biome biome, float ocean, float desert, float forest,
        float other, Biome otherBiome, float otherOcean, float otherDesert, float otherForest)
    {
        Assert.True(BitEqual(land, other), $"the land at ({x}, {z}) is {land}, not the {what} {other}");
        Assert.Equal(otherBiome, biome);
        Assert.True(BitEqual(ocean, otherOcean), $"the ocean weight at ({x}, {z}) moved against the {what}");
        Assert.True(BitEqual(desert, otherDesert), $"the desert weight at ({x}, {z}) moved against the {what}");
        Assert.True(BitEqual(forest, otherForest),
            $"the forest profile at ({x}, {z}) is {forest}, not the {what}'s {otherForest}");
    }

    [Fact]
    public void LandIsByteIdenticalOutsideEveryMassif()
    {
        var raised = 0;
        foreach (var s in Scan.Value)
        {
            if (TerrainRelief.SpawnClear(s.X, s.Z) == 0f)
                Assert.True(BitEqual(s.Massif, 0f), $"a massif rises at ({s.X}, {s.Z}) inside the spawn clearing");

            // The land without lone peaks carries the massifs and nothing else of this layer
            var pre = TerrainGenerator.PreFeatureLandHeight(s.X, s.Z, out var preBiome, out var preOcean,
                out var preDesert, out var preForest);
            var relief = TerrainGenerator.ReliefLandHeight(s.X, s.Z, out var reliefBiome, out var reliefOcean,
                out var reliefDesert, out var reliefForest);
            if (s.Massif > 0f)
            {
                if (pre > relief) raised++;
                continue;
            }

            AssertProfilesBitEqual(s.X, s.Z, "relief land", pre, preBiome, preOcean, preDesert, preForest,
                relief, reliefBiome, reliefOcean, reliefDesert, reliefForest);
            if (s.LonePeak > 0f) continue;

            // Outside both terms the full land is the relief land too
            var land = TerrainGenerator.LandHeight(s.X, s.Z, out var biome, out var ocean, out var desert, out var forest);
            AssertProfilesBitEqual(s.X, s.Z, "relief land", land, biome, ocean, desert, forest,
                relief, reliefBiome, reliefOcean, reliefDesert, reliefForest);
        }

        Assert.True(raised > 0, "no scan point stands on a massif that raises the land");
    }

    [Fact]
    public void LandIsByteIdenticalOutsideEveryLonePeak()
    {
        var raised = 0;
        foreach (var s in Scan.Value)
        {
            if (TerrainRelief.SpawnClear(s.X, s.Z) == 0f)
            {
                Assert.True(BitEqual(s.Massif, 0f), $"a massif rises at ({s.X}, {s.Z}) inside the spawn clearing");
                Assert.True(BitEqual(s.LonePeak, 0f), $"a lone peak rises at ({s.X}, {s.Z}) inside the spawn clearing");
            }

            var land = TerrainGenerator.LandHeight(s.X, s.Z, out var biome, out var ocean, out var desert, out var forest);
            var pre = TerrainGenerator.PreFeatureLandHeight(s.X, s.Z, out var preBiome, out var preOcean,
                out var preDesert, out var preForest);
            if (s.LonePeak > 0f)
            {
                if (land > pre) raised++;
                continue;
            }

            AssertProfilesBitEqual(s.X, s.Z, "land without lone peaks", land, biome, ocean, desert, forest,
                pre, preBiome, preOcean, preDesert, preForest);
        }

        Assert.True(raised > 0, "no scan point stands on a lone peak that raises the land");
    }

    [Fact]
    public void ALonePeakNeverStandsOnGroundThatFailsTheFlatnessTest()
    {
        var raised = Scan.Value.Where(s => s.LonePeak > 0f).ToArray();
        Assert.NotEmpty(raised);
        foreach (var s in raised)
        {
            var site = TerrainPeaks.PeakSiteAt(s.PeakCell.X, s.PeakCell.Z);
            Assert.True(site.Exists, $"the lone peak at ({s.X}, {s.Z}) comes from cell {s.PeakCell}, which has no site");
            Assert.True(DistanceSquared(s.X, s.Z, site) < site.Radius * site.Radius,
                $"({s.X}, {s.Z}) lies outside the radius {site.Radius} of its lone peak at ({site.CenterX}, {site.CenterZ})");
            TerrainSites.RimRange(site.CenterX, site.CenterZ, site.Radius, TerrainGenerator.PreFeatureHeightAt,
                out var rimMin, out var rimMax);
            Assert.True(rimMax - rimMin <= WorldGenerationConfig.PeakSiteFlatness,
                $"the lone peak at ({site.CenterX}, {site.CenterZ}) stands on ground spreading {rimMin}..{rimMax}");
            Assert.True(rimMin > WorldGenerationConfig.SeaLevel,
                $"the lone peak at ({site.CenterX}, {site.CenterZ}) stands on ground reaching down to {rimMin}");
        }

        // The rule is exercised: some raw candidate off the spawn stands on ground too rough for it,
        // and the cache rejects exactly that candidate
        const int cellBlocks = WorldGenerationConfig.PeakCellBlocks;
        var rejectedRough = false;
        for (var gx = -24; gx <= 24 && !rejectedRough; gx++)
        for (var gz = -24; gz <= 24 && !rejectedRough; gz++)
        {
            if (!TerrainSites.TryRoll(gx, gz, cellBlocks, TerrainPeaks.PeakSalt, WorldGenerationConfig.PeakChance,
                    WorldGenerationConfig.PeakSizeSkew, out _, out var centerX, out var centerZ, out var size))
                continue;
            if (TerrainRelief.SpawnClear(centerX, centerZ) <= 0f) continue;

            var nominal = MathUtils.Lerp(WorldGenerationConfig.PeakMinRadius, WorldGenerationConfig.PeakMaxRadius, size);
            TerrainSites.RimRange(centerX, centerZ, nominal, TerrainGenerator.PreFeatureHeightAt,
                out var rimMin, out var rimMax);
            if (rimMax - rimMin <= WorldGenerationConfig.PeakSiteFlatness) continue;

            Assert.False(TerrainPeaks.PeakSiteAt(gx, gz).Exists,
                $"lone-peak cell ({gx}, {gz}) is accepted on ground spreading {rimMin}..{rimMax}");
            rejectedRough = true;
        }

        Assert.True(rejectedRough, "no candidate off the spawn stands on ground too rough for a lone peak");
        _output.WriteLine($"{PeakSites.Value.Length} accepted lone peaks within ±{ScanHalfExtent} blocks; heights " +
            $"{PeakSites.Value.Min(s => s.Value.Height)}..{PeakSites.Value.Max(s => s.Value.Height)}, " +
            $"{raised.Length} raised scan points");
    }

    [Fact]
    public void NoLonePeakStandsOnAMassif()
    {
        var peaks = PeakSites.Value;
        Assert.NotEmpty(peaks);
        foreach (var (cell, site) in peaks)
            Assert.True(BitEqual(TerrainPeaks.MassifHeight(site.CenterX, site.CenterZ), 0f),
                $"lone peak {cell} at ({site.CenterX}, {site.CenterZ}) stands on a massif");
    }

    [Fact]
    public void LonePeakSitesNeverCrowdEachOther()
    {
        var peaks = PeakSites.Value;
        Assert.True(peaks.Length >= 2, $"only {peaks.Length} lone peaks to compare");
        for (var i = 0; i < peaks.Length; i++)
        for (var j = i + 1; j < peaks.Length; j++)
        {
            var distance = MathF.Sqrt(DistanceSquared(peaks[i].Value.CenterX, peaks[i].Value.CenterZ, peaks[j].Value));
            Assert.True(distance >= WorldGenerationConfig.PeakMinSpacing,
                $"lone peaks {peaks[i].Cell} and {peaks[j].Cell} stand {distance} blocks apart, " +
                $"closer than {WorldGenerationConfig.PeakMinSpacing}");
        }
    }

    [Fact]
    public void ASiteNeverLowersLand()
    {
        float[] fixedHeights = [-0f, 40f, 120f, 200f];
        foreach (var s in Scan.Value)
        {
            var relief = TerrainGenerator.ReliefLandHeight(s.X, s.Z);
            Assert.True(TerrainPeaks.WithMassif(s.X, s.Z, relief) >= relief,
                $"the massif lowers the relief land {relief} at ({s.X}, {s.Z})");
            foreach (var f in fixedHeights)
            {
                Assert.True(TerrainPeaks.WithMassif(s.X, s.Z, f) >= f, $"the massif lowers {f} at ({s.X}, {s.Z})");
                Assert.True(TerrainPeaks.WithLonePeak(s.X, s.Z, f) >= f, $"the lone peak lowers {f} at ({s.X}, {s.Z})");
            }
        }

        Assert.Contains(Scan.Value, s => s.Massif > 0f);
        Assert.Contains(Scan.Value, s => s.LonePeak > 0f);
    }

    // The largest change of the soft ceiling between neighboring samples of a 0..400 sweep.
    private static float CeilingMaxStep(int steps)
    {
        var max = 0f;
        var previous = TerrainPeaks.SoftCeiling(0f);
        for (var k = 1; k <= steps; k++)
        {
            var value = TerrainPeaks.SoftCeiling(400f * k / steps);
            max = MathF.Max(max, MathF.Abs(value - previous));
            previous = value;
        }

        return max;
    }

    [Fact]
    public void TheSoftCeilingIsIdentityBelowItsKneeMonotoneAndBelowTheCeiling()
    {
        const float knee0 = WorldGenerationConfig.ReliefCeilingHeight - WorldGenerationConfig.ReliefCeilingKnee;
        const int steps = 1600;
        var previous = float.MinValue;
        var aboveKnee = false;
        for (var k = 0; k <= steps; k++)
        {
            var h = 400f * k / steps;
            var value = TerrainPeaks.SoftCeiling(h);
            if (h <= knee0) Assert.True(BitEqual(value, h), $"the ceiling moves {h} below its knee to {value}");
            else aboveKnee = true;
            Assert.True(value >= previous, $"the ceiling falls from {previous} to {value} at {h}");
            Assert.True(value < WorldGenerationConfig.ReliefCeilingHeight,
                $"the ceiling of {h} reaches {value}, not below {WorldGenerationConfig.ReliefCeilingHeight}");
            previous = value;
        }

        Assert.True(aboveKnee, "the sweep never passes the knee");
        var coarse = CeilingMaxStep(800);
        var fine = CeilingMaxStep(1600);
        Assert.True(fine < coarse, $"the ceiling jumps: its largest step is {coarse} coarse and still {fine} fine");
    }

    [Fact]
    public void AMassifsSummitStandsAboveItsRim()
    {
        var sites = Sites.Value;
        Assert.NotEmpty(sites);
        var tallest = float.MinValue;
        foreach (var (cell, site) in sites)
        {
            var crown = CrownMax(site, PreFeature);
            var rim = RingHeights(site, site.Radius, 32, PreFeature).Max();
            Assert.True(crown > rim,
                $"massif {cell} at ({site.CenterX}, {site.CenterZ}) peaks at {crown}, not above its rim's {rim}");
            tallest = MathF.Max(tallest, crown);
        }

        _output.WriteLine($"{sites.Length} accepted massifs within ±{ScanHalfExtent} blocks; the tallest crown is " +
            $"{tallest}; rises {sites.Min(s => s.Value.Height)}..{sites.Max(s => s.Value.Height)}, " +
            $"radii {sites.Min(s => s.Value.Radius)}..{sites.Max(s => s.Value.Radius)}");
    }

    [Fact]
    public void AMassifsCrownIsAPeakNotAPlateau()
    {
        var sites = Sites.Value;
        Assert.NotEmpty(sites);
        foreach (var (cell, site) in sites)
        {
            var center = PreFeature(site.CenterX, site.CenterZ);
            var ring = RingHeights(site, site.Radius / 4f, 16, PreFeature).Average();
            Assert.True(center >= ring + site.Height / 10f,
                $"massif {cell} at ({site.CenterX}, {site.CenterZ}) stands {center} at its center, not a tenth of " +
                $"its rise {site.Height} above the {ring} a quarter of its radius out");
        }
    }

    [Fact]
    public void MassifTermsComeOnlyFromAcceptedSites()
    {
        var raised = Scan.Value.Where(s => s.Massif > 0f).ToArray();
        Assert.NotEmpty(raised);
        foreach (var s in raised)
        {
            var site = TerrainPeaks.MassifSiteAt(s.Cell.X, s.Cell.Z);
            Assert.True(site.Exists, $"the massif at ({s.X}, {s.Z}) comes from cell {s.Cell}, which has no site");
            var dx = s.X - site.CenterX;
            var dz = s.Z - site.CenterZ;
            Assert.True(dx * dx + dz * dz < site.Radius * site.Radius,
                $"({s.X}, {s.Z}) lies outside the radius {site.Radius} of its massif at ({site.CenterX}, {site.CenterZ})");
        }
    }

    [Fact]
    public void MassifSitesNeverCrowdEachOther()
    {
        var sites = Sites.Value;
        Assert.True(sites.Length >= 2, $"only {sites.Length} massifs to compare");
        for (var i = 0; i < sites.Length; i++)
        for (var j = i + 1; j < sites.Length; j++)
        {
            var a = sites[i].Value;
            var b = sites[j].Value;
            var dx = a.CenterX - b.CenterX;
            var dz = a.CenterZ - b.CenterZ;
            var distance = MathF.Sqrt(dx * dx + dz * dz);
            Assert.True(distance >= WorldGenerationConfig.MassifMinSpacing,
                $"massifs {sites[i].Cell} and {sites[j].Cell} stand {distance} blocks apart, " +
                $"closer than {WorldGenerationConfig.MassifMinSpacing}");
        }
    }

    [Fact]
    public void ASiteNeverStraddlesTheIslandFold()
    {
        var continental = 0;
        var island = 0;
        foreach (var (kind, sites) in new[] { ("massif", Sites.Value), ("lone peak", PeakSites.Value) })
        foreach (var (cell, site) in sites)
        {
            var centerPre = BiomeClassifier.PreIslandOceanWeight(site.CenterX, site.CenterZ);
            if (centerPre == 0f)
            {
                continental++;
                Assert.False(site.OnIsland, $"{kind} {cell} has a continental center but is marked on an island");
            }
            else
            {
                island++;
                Assert.True(site.OnIsland, $"{kind} {cell} has an off-continent center but is not marked on an island");
                _output.WriteLine($"island {kind} {cell} at ({site.CenterX}, {site.CenterZ}), height {site.Height}");
            }

            for (var k = 0; k < TerrainSites.RimPoints; k++)
            {
                TerrainSites.RimPoint(site.CenterX, site.CenterZ, site.Radius, k, out var rimX, out var rimZ);
                var pre = BiomeClassifier.PreIslandOceanWeight(rimX, rimZ);
                if (!site.OnIsland)
                {
                    Assert.True(pre == 0f,
                        $"continental {kind} {cell} has rim point ({rimX}, {rimZ}) off the continent (pre-island ocean {pre})");
                    continue;
                }

                Assert.True(pre > WorldGenerationConfig.OceanShoreWeight,
                    $"island {kind} {cell} has rim point ({rimX}, {rimZ}) at or inside the shore band (pre-island ocean {pre})");
                BiomeClassifier.BiomeWeights(rimX, rimZ, out var ocean, out _);
                Assert.True(ocean == 0f,
                    $"island {kind} {cell} has rim point ({rimX}, {rimZ}) off saturated island land (folded ocean {ocean})");
            }
        }

        _output.WriteLine($"{continental} continental sites, {island} island sites");
        Assert.True(continental > 0, "no continental site to check");
    }

    [Fact]
    public void AnIslandSiteNeverRaisesTheShoreBandOrTheContinent()
    {
        var islandMassifs = Sites.Value.Where(s => s.Value.OnIsland).ToArray();
        var islandPeaks = PeakSites.Value.Where(s => s.Value.OnIsland).ToArray();
        if (islandMassifs.Length + islandPeaks.Length == 0)
            _output.WriteLine("no island site at the default seed: only the continental half is checked");

        var points = new List<(float X, float Z)>();
        foreach (var s in Scan.Value) points.Add((s.X, s.Z));
        foreach (var (_, site) in islandMassifs.Concat(islandPeaks))
        {
            for (var dx = -site.Radius; dx <= site.Radius; dx += 8f)
            for (var dz = -site.Radius; dz <= site.Radius; dz += 8f)
            {
                if (dx * dx + dz * dz >= site.Radius * site.Radius) continue;
                points.Add((site.CenterX + dx, site.CenterZ + dz));
            }
        }

        var raisedByIsland = 0;
        var raisedByContinent = 0;
        foreach (var (x, z) in points)
        {
            var massif = TerrainPeaks.MassifHeight(x, z, out var massifCell);
            if (massif > 0f)
                Check(x, z, TerrainPeaks.MassifSiteAt(massifCell.X, massifCell.Z), "massif", massifCell);
            var peak = TerrainPeaks.LonePeakHeight(x, z, out var peakCell);
            if (peak > 0f)
                Check(x, z, TerrainPeaks.PeakSiteAt(peakCell.X, peakCell.Z), "lone peak", peakCell);
        }

        _output.WriteLine($"{islandMassifs.Length} island massifs, {islandPeaks.Length} island lone peaks; " +
                          $"{raisedByIsland} columns raised by an island site, {raisedByContinent} by a continental one");
        Assert.True(raisedByContinent > 0, "no column raised by a continental site to check");
        if (islandMassifs.Length + islandPeaks.Length > 0)
            Assert.True(raisedByIsland > 0, "island sites exist but no sampled column is raised by one");

        void Check(float x, float z, TerrainSite site, string kind, (int X, int Z) cell)
        {
            Assert.True(site.Exists, $"the {kind} at ({x}, {z}) comes from cell {cell}, which has no site");
            if (site.OnIsland)
            {
                raisedByIsland++;
                var pre = BiomeClassifier.PreIslandOceanWeight(x, z);
                Assert.True(pre > WorldGenerationConfig.OceanShoreWeight,
                    $"island {kind} {cell} raises ({x}, {z}), at or inside the shore band (pre-island ocean {pre})");
                return;
            }

            raisedByContinent++;
            Assert.True(BiomeClassifier.PreIslandOceanWeight(site.CenterX, site.CenterZ) == 0f,
                $"continental {kind} {cell} has an off-continent center ({site.CenterX}, {site.CenterZ})");
        }
    }

    [Fact]
    public void MassifSummitsStayUnderTheCloudLayer()
    {
        var sites = Sites.Value;
        Assert.NotEmpty(sites);
        foreach (var (cell, site) in sites)
        {
            var crown = CrownMax(site, Land);
            Assert.True(crown < SkyConfig.CloudHeight,
                $"massif {cell} at ({site.CenterX}, {site.CenterZ}) reaches {crown}, into the cloud layer at {SkyConfig.CloudHeight}");
        }

        var peaks = PeakSites.Value;
        Assert.NotEmpty(peaks);
        foreach (var (cell, site) in peaks)
        {
            var core = CrownMax(site, Land);
            Assert.True(core < SkyConfig.CloudHeight,
                $"lone peak {cell} at ({site.CenterX}, {site.CenterZ}) reaches {core}, into the cloud layer at {SkyConfig.CloudHeight}");
        }
    }
}
