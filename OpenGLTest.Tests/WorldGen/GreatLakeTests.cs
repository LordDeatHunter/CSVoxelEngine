using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features.Underground;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// The great lakes as the columns store them (<see cref="TerrainGenerator.Compute"/> through
/// <see cref="ColumnCache"/>), over a few real accepted sites, among them a river-fed lake with a river
/// ramp and a lake with an outflow: the stored water level is the one <see cref="WaterBodies.WaterLevelAt"/>
/// reports (which the far-LOD sheet reads per cell), the banks hold the lake's and its ramp rivers' water,
/// ramp water never stands more than a block over a wet neighbor, no river carves inside the lake, the bowl
/// reaches its floor, no pond shares a great lake's ground, a river meets its lake at the lake's own level
/// and an outflow carries water away from its lake no higher than the lake.
/// </summary>
public sealed class GreatLakeTests
{
    private readonly ITestOutputHelper _output;

    public GreatLakeTests(ITestOutputHelper output)
    {
        TestContent.EnsureLoaded();
        _output = output;
    }

    private const int MaxSites = 3;
    private const float Guard = WorldGenerationConfig.GreatLakeRavineGuardBlocks;
    private const float Threshold = WorldGenerationConfig.WaterBodyThreshold;

    // A window covers every lobe disc grown by the site's footprint, plus this many blocks (one chunk)
    private const float WindowMargin = Chunk.Size;

    /// <summary>One stored column of a window, with the water fields worldgen sampled there.</summary>
    private readonly record struct LakeColumn(
        int X,
        int Z,
        int Level,
        int Bed,
        bool Skip,
        float GreatWeight,
        float GreatGuard,
        bool InSite,
        float Gate,
        bool HoldsWater,
        float LakeWeight,
        float RiverWeight,
        int RiverLevel,
        float Outflow,
        bool RampWater)
    {
        /// <summary>A column of this site's standing water: its own great-lake weight floods it and its
        /// bed sits under its stored level.</summary>
        public bool IsLake => InSite && GreatWeight * Gate > Threshold && Bed < Level;
    }

    private readonly record struct Window((int X, int Z) Cell, GreatLakeSite Site, LakeColumn[] Columns);

    private static readonly Lazy<Window[]> Windows = new(BuildWindows);

    private static Window[] BuildWindows()
    {
        // The smallest lakes keep the windows cheap; the smallest river-fed lake with a ramp and the
        // smallest lake with an outflow are always among them
        var sorted = GreatLakeSiteTests.AcceptedSites.OrderBy(s => s.Value.Reach).ToArray();
        var chosen = new List<GreatLakeSiteTests.Site>();
        var riverFed = sorted.FirstOrDefault(s => s.Value.RiverFed && s.Value.RampLength > 0f);
        if (riverFed.Value.Exists) chosen.Add(riverFed);
        var outflow = sorted.FirstOrDefault(s => s.Value.HasOutflow);
        if (outflow.Value.Exists && !chosen.Contains(outflow)) chosen.Add(outflow);
        foreach (var site in sorted)
        {
            if (chosen.Count >= MaxSites) break;
            if (!chosen.Contains(site)) chosen.Add(site);
        }

        return chosen.Select(s => new Window(s.Cell, s.Value, ScanWindow(s.Cell, s.Value))).ToArray();
    }

    private static IEnumerable<GreatLakeLobe> Lobes(GreatLakeSite site)
    {
        for (var k = 0; k < site.LobeCount; k++)
            yield return site.Lobe(k);
    }

    // How far past its lobe edges a site's outputs reach: the ravine guard, or for a ramped site its
    // ramp, the bank cell past it and the ramp's guard
    private static float Footprint(GreatLakeSite site) =>
        site.RampLength > 0f ? site.RampLength + 1f + Guard : Guard;

    private static int Local(int x, int z) => ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);

    private static ColumnData ColumnAt(int x, int z) => ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);

    // An oasis and a hot spring carve their own pond and override the stored level after the lake carve
    // and the bank raise, by design (TerrainGenerator.Compute), so their columns are left out, both as the
    // checked column and as its neighbor
    private static bool IsSkipped(int x, int z) => ColumnAt(x, z).Oasis[Local(x, z)] || HotSpringGenerator.IsSite(x, z);

    private static (int X, int Z) Neighbor(int x, int z, int d) =>
        (x + (d == 0 ? 1 : d == 1 ? -1 : 0), z + (d == 2 ? 1 : d == 3 ? -1 : 0));

    // Every column of every chunk column that comes within the margin of a lobe disc grown by the footprint
    private static LakeColumn[] ScanWindow((int X, int Z) cell, GreatLakeSite site)
    {
        var lobes = Lobes(site).ToArray();
        var grow = Footprint(site) + WindowMargin;
        var minX = lobes.Min(l => l.X - l.Radius) - grow;
        var maxX = lobes.Max(l => l.X + l.Radius) + grow;
        var minZ = lobes.Min(l => l.Z - l.Radius) - grow;
        var maxZ = lobes.Max(l => l.Z + l.Radius) + grow;

        var columns = new List<LakeColumn>();
        for (var cx = MathUtils.FloorToInt(minX / Chunk.Size); cx <= MathUtils.FloorToInt(maxX / Chunk.Size); cx++)
        for (var cz = MathUtils.FloorToInt(minZ / Chunk.Size); cz <= MathUtils.FloorToInt(maxZ / Chunk.Size); cz++)
        {
            if (!ChunkNearAnyLobe(cx, cz, lobes, grow)) continue;

            var column = ColumnCache.Get(cx, cz);
            for (var i = 0; i < column.Heights.Length; i++)
            {
                var x = cx * Chunk.Size + (i >> Chunk.SizeShift);
                var z = cz * Chunk.Size + (i & Chunk.SizeMask);
                var weight = GreatLakes.Sample(x, z, out _, out _, out var guard, out var riverLevel, out _,
                    out var outflow, out var owner);
                var biome = TerrainGenerator.BiomeWeights(x, z, out _, out var desert);
                var holds = WaterBodies.CanHoldSurfaceWater(biome);
                var lake = 0f;
                var river = 0f;
                if (holds) WaterBodies.SampleSurfaceWater(x, z, out lake, out _, out _, out river, out _, out _, out _);

                var level = column.WaterLevel[i];
                var bed = column.Bed[i];
                var inSite = owner == cell;
                var gate = WaterBodies.SurfaceWaterGate(desert);
                // A column of this site's ramp river or outflow: river water above sea level that its own
                // great-lake weight does not flood
                var rampWater = (inSite || GreatLakes.Beyond(site, x, z) < Footprint(site))
                                && river * gate > Threshold && bed < level && weight * gate <= Threshold
                                && level > WorldGenerationConfig.SeaLevel;
                columns.Add(new LakeColumn(x, z, level, bed, column.Oasis[i] || HotSpringGenerator.IsSite(x, z),
                    weight, guard, inSite, gate, holds, lake, river, riverLevel, outflow, rampWater));
            }
        }

        return columns.ToArray();
    }

    private static bool ChunkNearAnyLobe(int cx, int cz, GreatLakeLobe[] lobes, float grow)
    {
        foreach (var lobe in lobes)
        {
            // The nearest point of the chunk column's square to the lobe center
            var nx = Math.Clamp(lobe.X, cx * Chunk.Size, cx * Chunk.Size + Chunk.Size);
            var nz = Math.Clamp(lobe.Z, cz * Chunk.Size, cz * Chunk.Size + Chunk.Size);
            var dx = nx - lobe.X;
            var dz = nz - lobe.Z;
            var reach = lobe.Radius + grow;
            if (dx * dx + dz * dz <= reach * reach) return true;
        }

        return false;
    }

    private Window[] Sites
    {
        get
        {
            var windows = Windows.Value;
            foreach (var w in windows)
                _output.WriteLine($"great lake in cell {w.Cell}: reach {w.Site.Reach:F0}, surface {w.Site.Surface}, "
                                  + $"floor {w.Site.Floor}, {(w.Site.RiverFed ? "river-fed" : "rim-fed")}, "
                                  + $"ramp {w.Site.RampLength:F0}, outflow {w.Site.OutflowLength:F0}, "
                                  + $"{w.Columns.Length} columns, {w.Columns.Count(c => c.IsLake)} lake columns, "
                                  + $"{w.Columns.Count(c => c.RampWater)} ramp water columns");
            Assert.NotEmpty(windows);
            return windows;
        }
    }

    [Fact]
    public void TheStoredLevelIsTheLevelWaterLevelAtReports()
    {
        var lakeColumns = 0;
        foreach (var (cell, _, columns) in Sites)
        foreach (var c in columns)
        {
            if (c.Skip) continue;
            if (c.IsLake) lakeColumns++;
            var reported = WaterBodies.WaterLevelAt(c.X, c.Z);
            Assert.True(c.Level == reported,
                $"the column ({c.X}, {c.Z}) by the great lake in cell {cell} stores level {c.Level}, "
                + $"but WaterLevelAt reports {reported}");
        }

        Assert.True(lakeColumns > 0, "no great-lake column in the scanned windows");
    }

    [Fact]
    public void GreatLakeBanksStandAtOrAboveTheWater()
    {
        // The leak rule of LakeTests.PondBanksStandAtOrAboveTheWater, over the lake's own water and its
        // ramp rivers: a neighbor that holds another level and is not itself under water must stand at or
        // above this water (the bank raise in TerrainGenerator.Compute is what holds a ramp step)
        var leaks = 0;
        var checkedColumns = 0;
        var rampColumns = 0;
        foreach (var (cell, site, columns) in Sites)
        foreach (var c in columns)
        {
            if (c.Skip || c.Bed >= c.Level || (c.Level != site.Surface && !c.RampWater)) continue;

            checkedColumns++;
            if (c.RampWater) rampColumns++;
            for (var d = 0; d < 4; d++)
            {
                var (nx, nz) = Neighbor(c.X, c.Z, d);
                if (IsSkipped(nx, nz)) continue;

                var column = ColumnAt(nx, nz);
                var local = Local(nx, nz);
                var neighborLevel = column.WaterLevel[local];
                if (neighborLevel == c.Level) continue;
                if (column.Bed[local] < neighborLevel) continue;
                if (column.Heights[local] >= c.Level) continue;

                leaks++;
                if (leaks <= 10)
                    _output.WriteLine($"leak from ({c.X}, {c.Z}) at {c.Level} (cell {cell}) into ({nx}, {nz}), "
                                      + $"height {column.Heights[local]}, level {neighborLevel}");
            }
        }

        Assert.True(checkedColumns > 0, "no great-lake water column in the scanned windows");
        Assert.True(rampColumns > 0, "no ramp river column in the scanned windows");
        Assert.Equal(0, leaks);
    }

    [Fact]
    public void NoRampWaterStandsMoreThanABlockOverItsWetNeighbor()
    {
        var rampColumns = 0;
        var steps = 0;
        foreach (var (cell, _, columns) in Sites)
        foreach (var c in columns)
        {
            if (c.Skip || !c.RampWater) continue;

            rampColumns++;
            for (var d = 0; d < 4; d++)
            {
                var (nx, nz) = Neighbor(c.X, c.Z, d);
                if (IsSkipped(nx, nz)) continue;

                var column = ColumnAt(nx, nz);
                var local = Local(nx, nz);
                var neighborLevel = column.WaterLevel[local];
                if (column.Bed[local] >= neighborLevel) continue;

                if (neighborLevel != c.Level) steps++;
                Assert.True(Math.Abs(neighborLevel - c.Level) <= 1,
                    $"ramp water at ({c.X}, {c.Z}) by the great lake in cell {cell} stands at {c.Level}, "
                    + $"but its wet neighbor ({nx}, {nz}) at {neighborLevel}");
            }
        }

        _output.WriteLine($"{rampColumns} ramp water columns, {steps} steps to a wet neighbor");
        Assert.True(rampColumns > 0, "no ramp river column in the scanned windows");
        Assert.True(steps > 0, "no ramp water column steps to a wet neighbor in the scanned windows");
    }

    [Fact]
    public void NoRiverCarvesInsideAGreatLake()
    {
        var lakeColumns = 0;
        foreach (var (cell, _, columns) in Sites)
        foreach (var c in columns)
        {
            if (!c.IsLake || !c.HoldsWater) continue;

            lakeColumns++;
            Assert.True(c.RiverWeight == 0f,
                $"a river of weight {c.RiverWeight} carves at ({c.X}, {c.Z}) inside the great lake in cell {cell}");
        }

        Assert.True(lakeColumns > 0, "no great-lake column in the scanned windows");
    }

    [Fact]
    public void TheBowlIsDugDownToItsFloor()
    {
        foreach (var (cell, site, columns) in Sites)
        {
            var lake = columns.Where(c => c.IsLake && !c.Skip).ToArray();
            Assert.True(lake.Length > 0, $"the great lake in cell {cell} has no lake column");
            var deepest = lake.Min(c => c.Bed);
            Assert.True(deepest <= site.Floor,
                $"the great lake in cell {cell} is dug only to {deepest}, above its floor {site.Floor}");
        }
    }

    [Fact]
    public void NoPondShareAGreatLakesGround()
    {
        var greatColumns = 0;
        foreach (var (cell, _, columns) in Sites)
        foreach (var c in columns)
        {
            // Where the great lake has no weight, guard, outflow or ramp level the rule holds trivially
            if (c.GreatWeight == 0f && c.GreatGuard == 0f && c.Outflow == 0f
                && c.RiverLevel == WorldGenerationConfig.SeaLevel)
                continue;

            greatColumns++;
            var pond = WaterBodies.LakeSample(c.X, c.Z, out _, out _);
            Assert.True(pond == 0f,
                $"a pond of weight {pond} reaches ({c.X}, {c.Z}) on the ground of the great lake in cell {cell}");
        }

        Assert.True(greatColumns > 0, "no column in the scanned windows lies on great-lake ground");
    }

    [Fact]
    public void ARiverMeetsItsLakeAtTheLakesOwnLevel()
    {
        var meetings = 0;
        var elevatedMeetings = 0;
        foreach (var (cell, site, columns) in Sites)
        foreach (var c in columns)
        {
            if (c.Skip || !c.HoldsWater || !c.InSite) continue;
            if (c.RiverWeight * c.Gate <= Threshold || c.LakeWeight <= 0f) continue;

            meetings++;
            if (site.Surface > WorldGenerationConfig.SeaLevel) elevatedMeetings++;
            Assert.True(c.Level == site.Surface,
                $"where a river meets the great lake in cell {cell} at ({c.X}, {c.Z}) the water stands "
                + $"at {c.Level}, not the lake's {site.Surface}");
        }

        _output.WriteLine($"{meetings} columns where a river meets its great lake, {elevatedMeetings} above sea level");
        Assert.True(meetings > 0, "no river meets a great lake in the scanned windows");
        Assert.True(elevatedMeetings > 0, "no river meets a great lake above sea level in the scanned windows");
    }

    [Fact]
    public void AnOutflowCarriesWaterAwayFromItsLake()
    {
        var outflows = Sites.Where(w => w.Site.HasOutflow).ToArray();
        Assert.NotEmpty(outflows);

        var outflowColumns = 0;
        foreach (var (cell, site, columns) in outflows)
        foreach (var c in columns)
        {
            if (c.Skip || c.Outflow * c.Gate <= Threshold || c.Bed >= c.Level) continue;
            if (c.GreatWeight * c.Gate > Threshold) continue;

            outflowColumns++;
            Assert.True(c.Level <= site.Surface,
                $"the outflow of the great lake in cell {cell} stands at {c.Level} at ({c.X}, {c.Z}), "
                + $"above the lake's {site.Surface}");
        }

        _output.WriteLine($"{outflowColumns} outflow water columns outside the lakes");
        Assert.True(outflowColumns > 0, "no outflow carries water out of its lake in the scanned windows");
    }
}
