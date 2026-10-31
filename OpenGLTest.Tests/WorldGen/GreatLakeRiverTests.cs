using System;
using System.Linq;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// The great-lake river ramps and outflows at the site and sample level: an elevated lake a river
/// reaches keeps its own surface when it has a ramp, the ramp's river level (<see cref="GreatLakes.RiverLevelAt"/>)
/// steps down a block at a time from the lake's surface to the sea and never rises away from it, two
/// neighboring columns in a ramp differ by at most a block, an outflow leaves from the lowest eligible
/// rim point radially away from its lobe, its level never steps up along it, and its ground stands over
/// its level. Over the sites <see cref="GreatLakeSiteTests.AcceptedSites"/> found in the ±16000 box.
/// </summary>
public sealed class GreatLakeRiverTests
{
    public GreatLakeRiverTests()
    {
        TestContent.EnsureLoaded();
    }

    private const int SeaLevel = WorldGenerationConfig.SeaLevel;
    private const float HalfWidth = WorldGenerationConfig.GreatLakeOutflowHalfWidth;
    private const int RampSampleStride = 5;

    private static GreatLakeSiteTests.Site[] Ramped =>
        GreatLakeSiteTests.AcceptedSites.Where(s => s.Value.RampLength > 0f).ToArray();

    private static GreatLakeSiteTests.Site[] WithOutflow =>
        GreatLakeSiteTests.AcceptedSites.Where(s => s.Value.HasOutflow).ToArray();

    private static float Land(float x, float z) => TerrainGenerator.LandHeight(x, z, out _, out _, out _, out _);

    private static int SampledRiverLevel(float x, float z)
    {
        GreatLakes.Sample(x, z, out _, out _, out _, out var riverLevel, out _, out _, out _);
        return riverLevel;
    }

    // The outflow's axis and the two lines HalfWidth to either side of it, as (offset) values
    private static readonly float[] OutflowLines = [-HalfWidth, 0f, HalfWidth];

    private static (float X, float Z) OnOutflow(in GreatLakeSite site, float s, float offset) =>
        (site.OutflowX + site.OutflowDirX * s - site.OutflowDirZ * offset,
            site.OutflowZ + site.OutflowDirZ * s + site.OutflowDirX * offset);

    [Fact]
    public void AnElevatedLakeARiverReachesKeepsItsOwnSurface()
    {
        var sites = GreatLakeSiteTests.AcceptedSites;
        Assert.Contains(sites, s => s.Value.RiverFed && s.Value.RampLength > 0f);

        foreach (var (cell, site) in sites)
        {
            if (site.RampLength > 0f)
                Assert.True(site.Surface > SeaLevel,
                    $"the ramped great lake in cell {cell} rests at {site.Surface}, not above the sea");
            if (site.RiverFed && site.RampLength <= 0f)
                Assert.True(site.Surface == SeaLevel,
                    $"the river-fed great lake in cell {cell} has no ramp but rests at {site.Surface}, not at the sea");
            if (site.HasOutflow)
                Assert.True(site.RampLength > 0f, $"the great lake in cell {cell} has an outflow but no river ramp");
        }
    }

    [Fact]
    public void TheRiverLevelStepsDownFromTheSurfaceToTheSea()
    {
        var sites = Ramped;
        Assert.NotEmpty(sites);
        foreach (var (cell, site) in sites)
        {
            var previous = int.MaxValue;
            for (var beyond = -10f; beyond <= site.RampLength + 10f; beyond += 0.25f)
            {
                var level = GreatLakes.RiverLevelAt(site, beyond);
                Assert.True(level <= previous,
                    $"the ramp of the great lake in cell {cell} rises from {previous} to {level} at {beyond} past its edge");
                previous = level;

                if (beyond <= 0f)
                    Assert.True(level == site.Surface,
                        $"the ramp of the great lake in cell {cell} reads {level} inside its edge, not its surface {site.Surface}");
                if (beyond >= site.RampLength)
                    Assert.True(level == SeaLevel,
                        $"the ramp of the great lake in cell {cell} reads {level} past its end, not the sea level");

                var next = GreatLakes.RiverLevelAt(site, beyond + 1f);
                Assert.True(level - next <= 1,
                    $"the ramp of the great lake in cell {cell} drops from {level} to {next} within a block at {beyond}");
            }

            Assert.True(GreatLakes.RiverLevelAt(site, site.RampLength - 0.25f) > SeaLevel,
                $"the ramp of the great lake in cell {cell} reaches the sea before its end");
        }
    }

    [Fact]
    public void NeighboringColumnsInARampDifferByAtMostOneBlock()
    {
        var sites = Ramped;
        Assert.NotEmpty(sites);
        var steps = 0;
        foreach (var (cell, site) in sites)
        {
            var lobes = Enumerable.Range(0, site.LobeCount).Select(k => site.Lobe(k)).ToArray();
            var grow = site.RampLength + 2f;
            var minX = MathUtils.FloorToInt(lobes.Min(l => l.X - l.Radius) - grow);
            var maxX = MathUtils.FloorToInt(lobes.Max(l => l.X + l.Radius) + grow);
            var minZ = MathUtils.FloorToInt(lobes.Min(l => l.Z - l.Radius) - grow);
            var maxZ = MathUtils.FloorToInt(lobes.Max(l => l.Z + l.Radius) + grow);
            for (var x = minX; x <= maxX; x += RampSampleStride)
            for (var z = minZ; z <= maxZ; z += RampSampleStride)
            {
                var beyond = GreatLakes.Beyond(site, x, z);
                if (beyond < -2f || beyond > site.RampLength + 2f) continue;

                var level = SampledRiverLevel(x, z);
                foreach (var (nx, nz) in new[] { (x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1) })
                {
                    var neighbor = SampledRiverLevel(nx, nz);
                    Assert.True(Math.Abs(level - neighbor) <= 1,
                        $"in the ramp of the great lake in cell {cell} the river level is {level} at ({x}, {z}) "
                        + $"but {neighbor} at ({nx}, {nz})");
                    if (level != neighbor) steps++;
                }
            }
        }

        Assert.True(steps > 0, "no sampled ramp column stands beside a step of its river level");
    }

    [Fact]
    public void AnOutflowLeavesThroughTheLowSideOfItsLake()
    {
        var sites = WithOutflow;
        Assert.NotEmpty(sites);
        foreach (var (cell, site) in sites)
        {
            var dirLength = MathF.Sqrt(site.OutflowDirX * site.OutflowDirX + site.OutflowDirZ * site.OutflowDirZ);
            Assert.True(MathF.Abs(dirLength - 1f) < 1e-4f,
                $"the outflow of the great lake in cell {cell} runs along a direction of length {dirLength}");

            var onRing = Enumerable.Range(0, site.LobeCount).Select(k => site.Lobe(k)).Any(lobe =>
            {
                float dx = site.OutflowX - lobe.X, dz = site.OutflowZ - lobe.Z;
                return MathF.Abs(MathF.Sqrt(dx * dx + dz * dz) - (lobe.Radius + 1f)) <= 1f;
            });
            Assert.True(onRing, $"the outflow of the great lake in cell {cell} does not start a block past a lobe edge");

            var start = Land(site.OutflowX, site.OutflowZ);
            Assert.True(start >= site.Surface,
                $"the outflow of the great lake in cell {cell} starts on ground at {start}, under the lake's surface {site.Surface}");

            var candidates = 0;
            for (var k = 0; k < site.LobeCount; k++)
            {
                var lobe = site.Lobe(k);
                var count = GreatLakes.OutflowRingCount(lobe);
                for (var i = 0; i < count; i++)
                {
                    var (x, z) = GreatLakes.OutflowRingPoint(lobe, i, count);
                    if (!GreatLakes.IsOutflowCandidate(site, k, x, z)) continue;

                    candidates++;
                    var land = Land(x, z);
                    Assert.True(start <= land,
                        $"the outflow of the great lake in cell {cell} starts on ground at {start}, "
                        + $"above the eligible rim point ({x}, {z}) at {land}");
                }
            }

            Assert.True(candidates > 0, $"the great lake in cell {cell} has an outflow but no eligible rim point");
        }
    }

    [Fact]
    public void AnOutflowNeverStepsUpAwayFromItsLake()
    {
        var sites = WithOutflow;
        Assert.NotEmpty(sites);
        var descents = 0;
        foreach (var (cell, site) in sites)
        foreach (var offset in OutflowLines)
        {
            var previous = int.MaxValue;
            for (var s = 0f; s <= site.OutflowLength; s += 1f)
            {
                var (x, z) = OnOutflow(site, s, offset);
                var level = SampledRiverLevel(x, z);
                Assert.True(level <= previous,
                    $"the outflow of the great lake in cell {cell} steps up from {previous} to {level} "
                    + $"at {s} along it, {offset} off its axis");
                if (level < previous && previous != int.MaxValue) descents++;
                previous = level;
            }
        }

        Assert.True(descents > 0, "no outflow steps down along its length");
    }

    [Fact]
    public void AnOutflowsGroundStandsOverItsLevel()
    {
        var sites = WithOutflow;
        Assert.NotEmpty(sites);
        var checkedPoints = 0;
        foreach (var (cell, site) in sites)
        foreach (var offset in OutflowLines)
        {
            for (var s = 0f; s <= site.OutflowLength; s += 2f)
            {
                var (x, z) = OnOutflow(site, s, offset);
                var beyond = GreatLakes.Beyond(site, x, z);
                if (beyond <= 1f) continue;

                checkedPoints++;
                var level = GreatLakes.RiverLevelAt(site, beyond);
                var land = Land(x, z);
                Assert.True(land >= level,
                    $"the outflow of the great lake in cell {cell} crosses ground at {land} under its level {level} "
                    + $"at ({x}, {z})");
            }
        }

        Assert.True(checkedPoints > 0, "no outflow point lies past its lake's edge");
    }
}
