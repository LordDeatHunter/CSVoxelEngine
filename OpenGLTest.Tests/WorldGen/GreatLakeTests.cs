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
/// <see cref="ColumnCache"/>), over a few real accepted sites: the stored water level is the one
/// <see cref="WaterBodies.WaterLevelAt"/> reports (which the far-LOD sheet reads per cell), the banks
/// hold the water, no river carves inside the lake, the bowl reaches its floor, no pond shares a great
/// lake's ground, and a river meets a river-fed lake at the lake's own level.
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

    // A window covers every lobe disc grown by the guard, plus this many blocks (one chunk)
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
        float RiverWeight)
    {
        /// <summary>A column of this site's standing water: its own great-lake weight floods it and its
        /// bed sits under its stored level.</summary>
        public bool IsLake => InSite && GreatWeight * Gate > WorldGenerationConfig.WaterBodyThreshold && Bed < Level;
    }

    private readonly record struct Window((int X, int Z) Cell, GreatLakeSite Site, LakeColumn[] Columns);

    private static readonly Lazy<Window[]> Windows = new(BuildWindows);

    private static Window[] BuildWindows()
    {
        // The smallest lakes keep the windows cheap; the smallest river-fed one is always among them
        var sorted = GreatLakeSiteTests.AcceptedSites.OrderBy(s => s.Value.Reach).ToArray();
        var chosen = new List<GreatLakeSiteTests.Site>();
        var riverFed = sorted.FirstOrDefault(s => s.Value.RiverFed);
        if (riverFed.Value.Exists) chosen.Add(riverFed);
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

    // Every column of every chunk column that comes within the margin of a lobe disc grown by the guard
    private static LakeColumn[] ScanWindow((int X, int Z) cell, GreatLakeSite site)
    {
        var lobes = Lobes(site).ToArray();
        var minX = lobes.Min(l => l.X - l.Radius) - Guard - WindowMargin;
        var maxX = lobes.Max(l => l.X + l.Radius) + Guard + WindowMargin;
        var minZ = lobes.Min(l => l.Z - l.Radius) - Guard - WindowMargin;
        var maxZ = lobes.Max(l => l.Z + l.Radius) + Guard + WindowMargin;

        var columns = new List<LakeColumn>();
        for (var cx = MathUtils.FloorToInt(minX / Chunk.Size); cx <= MathUtils.FloorToInt(maxX / Chunk.Size); cx++)
        for (var cz = MathUtils.FloorToInt(minZ / Chunk.Size); cz <= MathUtils.FloorToInt(maxZ / Chunk.Size); cz++)
        {
            if (!ChunkNearAnyLobe(cx, cz, lobes)) continue;

            var column = ColumnCache.Get(cx, cz);
            for (var i = 0; i < column.Heights.Length; i++)
            {
                var x = cx * Chunk.Size + (i >> Chunk.SizeShift);
                var z = cz * Chunk.Size + (i & Chunk.SizeMask);
                var weight = GreatLakes.Sample(x, z, out _, out _, out var guard, out var owner);
                var biome = TerrainGenerator.BiomeWeights(x, z, out _, out var desert);
                var holds = WaterBodies.CanHoldSurfaceWater(biome);
                var lake = 0f;
                var river = 0f;
                if (holds) WaterBodies.SampleSurfaceWater(x, z, out lake, out _, out _, out river, out _);

                // An oasis and a hot spring carve their own pond and override the stored level after the
                // lake carve, by design (TerrainGenerator.Compute), so their columns are left out
                var skip = column.Oasis[i] || HotSpringGenerator.IsSite(x, z);
                columns.Add(new LakeColumn(x, z, column.WaterLevel[i], column.Bed[i], skip, weight, guard,
                    owner == cell, WaterBodies.SurfaceWaterGate(desert), holds, lake, river));
            }
        }

        return columns.ToArray();
    }

    private static bool ChunkNearAnyLobe(int cx, int cz, GreatLakeLobe[] lobes)
    {
        foreach (var lobe in lobes)
        {
            // The nearest point of the chunk column's square to the lobe center
            var nx = Math.Clamp(lobe.X, cx * Chunk.Size, cx * Chunk.Size + Chunk.Size);
            var nz = Math.Clamp(lobe.Z, cz * Chunk.Size, cz * Chunk.Size + Chunk.Size);
            var dx = nx - lobe.X;
            var dz = nz - lobe.Z;
            var reach = lobe.Radius + Guard + WindowMargin;
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
                                  + $"{w.Columns.Length} columns, {w.Columns.Count(c => c.IsLake)} lake columns");
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
        // The leak rule of LakeTests.PondBanksStandAtOrAboveTheWater: a neighbor that holds another
        // level and is not itself under water must stand at or above this water
        var leaks = 0;
        var checkedColumns = 0;
        foreach (var (cell, site, columns) in Sites)
        foreach (var c in columns)
        {
            if (c.Skip || c.Level != site.Surface || c.Bed >= c.Level) continue;

            checkedColumns++;
            for (var d = 0; d < 4; d++)
            {
                var nx = c.X + (d == 0 ? 1 : d == 1 ? -1 : 0);
                var nz = c.Z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                var column = ColumnCache.Get(nx >> Chunk.SizeShift, nz >> Chunk.SizeShift);
                var local = ((nx & Chunk.SizeMask) << Chunk.SizeShift) | (nz & Chunk.SizeMask);
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
        Assert.Equal(0, leaks);
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
            // Where the great lake has neither weight nor guard the rule holds trivially
            if (c.GreatWeight == 0f && c.GreatGuard == 0f) continue;

            greatColumns++;
            var pond = WaterBodies.LakeSample(c.X, c.Z, out _, out _);
            Assert.True(pond == 0f,
                $"a pond of weight {pond} reaches ({c.X}, {c.Z}) on the ground of the great lake in cell {cell}");
        }

        Assert.True(greatColumns > 0, "no column in the scanned windows lies on great-lake ground");
    }

    [Fact]
    public void ARiverMeetsARiverFedLakeAtItsOwnLevel()
    {
        var sites = Sites;
        var riverFed = sites.Where(w => w.Site.RiverFed).ToArray();
        Assert.NotEmpty(riverFed);

        var meetings = 0;
        foreach (var (cell, site, columns) in riverFed)
        foreach (var c in columns)
        {
            if (c.Skip || !c.HoldsWater || !c.InSite) continue;
            if (c.RiverWeight * c.Gate <= WorldGenerationConfig.WaterBodyThreshold || c.LakeWeight <= 0f) continue;

            meetings++;
            Assert.True(c.Level == site.Surface,
                $"where a river meets the river-fed great lake in cell {cell} at ({c.X}, {c.Z}) the water stands "
                + $"at {c.Level}, not the lake's {site.Surface}");
        }

        _output.WriteLine($"{meetings} columns where a river meets a river-fed great lake");
        Assert.True(meetings > 0, "no river meets a river-fed great lake in the scanned windows");
    }
}
