using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// The relief layer is a pure function of position and the world seed: a seed swap changes it,
/// and swapping back reproduces it bit for bit. The hashed massif and lone-peak site caches follow
/// the seed too, so a cached verdict never outlives the seed it was computed under. The island term
/// and its hashed archipelagos follow the seed the same way.
/// </summary>
[Collection("Worldgen seed")]
public sealed class TerrainReliefSeedTests
{
    public TerrainReliefSeedTests() => TestContent.EnsureLoaded();

    private const int GridHalfExtent = 12000;
    private const int GridStride = 1500;

    // The massif and lone-peak cells the site samples cover
    private const int MassifCellHalfExtent = 8;
    private const int MassifCellStride = 2;
    private const int PeakCellHalfExtent = 24;
    private const int PeakCellStride = 6;

    private static IEnumerable<(int X, int Z)> Cells(int halfExtent, int stride)
    {
        for (var gx = -halfExtent; gx <= halfExtent; gx += stride)
        for (var gz = -halfExtent; gz <= halfExtent; gz += stride)
            yield return (gx, gz);
    }

    private static IEnumerable<(int X, int Z)> MassifCells => Cells(MassifCellHalfExtent, MassifCellStride);
    private static IEnumerable<(int X, int Z)> PeakCells => Cells(PeakCellHalfExtent, PeakCellStride);

    private static void AddSite(List<(string, int)> samples, string kind, (int X, int Z) cell, TerrainSite site)
    {
        samples.Add(($"{kind} site exists in cell {cell}", site.Exists ? 1 : 0));
        samples.Add(($"{kind} site center x in cell {cell}", BitConverter.SingleToInt32Bits(site.CenterX)));
        samples.Add(($"{kind} site height in cell {cell}", BitConverter.SingleToInt32Bits(site.Height)));
    }

    // The cached massif and lone-peak verdicts over the site grid, as raw bits
    private static List<(string Term, int Bits)> SampleSites()
    {
        var samples = new List<(string, int)>();
        foreach (var cell in MassifCells)
            AddSite(samples, "massif", cell, TerrainPeaks.MassifSiteAt(cell.X, cell.Z));
        foreach (var cell in PeakCells)
            AddSite(samples, "lone peak", cell, TerrainPeaks.PeakSiteAt(cell.X, cell.Z));
        return samples;
    }

    // HighlandWeight and the full-highland MountainHeight at every grid point, plus the site verdicts, as raw bits
    private static List<(string Term, int Bits)> SampleRelief()
    {
        var samples = new List<(string, int)>();
        for (var x = -GridHalfExtent; x <= GridHalfExtent; x += GridStride)
        for (var z = -GridHalfExtent; z <= GridHalfExtent; z += GridStride)
        {
            samples.Add(($"highland weight at ({x}, {z})",
                BitConverter.SingleToInt32Bits(TerrainRelief.HighlandWeight(x, z))));
            samples.Add(($"range height at ({x}, {z})",
                BitConverter.SingleToInt32Bits(TerrainRelief.MountainHeight(x, z, 1f))));
        }

        samples.AddRange(SampleSites());
        return samples;
    }

    // Every cached verdict on the grid is exactly what its builder computes under the current seed
    private static void AssertCachesMatchBuilders(string when)
    {
        foreach (var cell in MassifCells)
            Assert.True(TerrainPeaks.MassifSiteAt(cell.X, cell.Z) == TerrainPeaks.BuildMassifSite(cell),
                $"the cached massif site in cell {cell} is stale {when}");
        foreach (var cell in PeakCells)
            Assert.True(TerrainPeaks.PeakSiteAt(cell.X, cell.Z) == TerrainPeaks.BuildPeakSite(cell),
                $"the cached lone-peak site in cell {cell} is stale {when}");
    }

    [Fact]
    public void TheReliefIsAPureFunctionOfPositionAndSeed()
    {
        Assert.False(WorldSeed.WorldIsLive);
        try
        {
            var first = SampleRelief();
            WorldSeed.Apply(WorldSeed.Default + 7);
            var other = SampleRelief();
            WorldSeed.Apply(WorldSeed.Default);
            var again = SampleRelief();

            for (var i = 0; i < first.Count; i++)
                Assert.True(first[i].Bits == again[i].Bits, $"the {first[i].Term} differs after swapping the seed back");
            Assert.False(first.Select(s => s.Bits).SequenceEqual(other.Select(s => s.Bits)),
                "the relief did not change with the seed");
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    // The island points: grid points at least IslandMinDistance out (past the spawn clearing) whose
    // pre-island ocean lies past the shore band, chosen once under the default seed
    private const int IslandGridHalfExtent = 12000;
    private const int IslandGridStride = 600;
    private const float IslandMinDistance = 3000f;
    private const int ArchipelagoCellHalfExtent = 6;
    private const int ArchipelagoCellsSampled = 4;

    private static List<(float X, float Z)> IslandPoints()
    {
        var points = new List<(float, float)>();
        for (var x = -IslandGridHalfExtent; x <= IslandGridHalfExtent; x += IslandGridStride)
        for (var z = -IslandGridHalfExtent; z <= IslandGridHalfExtent; z += IslandGridStride)
        {
            if (MathF.Sqrt((float)x * x + (float)z * z) < IslandMinDistance) continue;
            if (BiomeClassifier.PreIslandOceanWeight(x, z) <= WorldGenerationConfig.OceanShoreWeight) continue;
            points.Add((x, z));
        }

        return points;
    }

    private static List<(int X, int Z)> ArchipelagoCells()
    {
        var cells = new List<(int, int)>();
        foreach (var cell in Cells(ArchipelagoCellHalfExtent, 1))
        {
            if (!TerrainIslands.TryGetArchipelago(cell.X, cell.Z, out _, out _, out _, out _, out _)) continue;
            cells.Add(cell);
            if (cells.Count == ArchipelagoCellsSampled) break;
        }

        return cells;
    }

    // The island land weight at full ocean depth and the archipelago weight at every point and footprint
    // center, plus every archipelago cell's roll, as raw bits
    private static List<(string Term, int Bits)> SampleIslands(List<(float X, float Z)> points, List<(int X, int Z)> cells,
        List<(float X, float Z)> centers)
    {
        var samples = new List<(string, int)>();
        foreach (var (x, z) in points.Concat(centers))
        {
            samples.Add(($"island land weight at ({x}, {z})",
                BitConverter.SingleToInt32Bits(TerrainIslands.LandWeight(x, z, 1f))));
            samples.Add(($"archipelago weight at ({x}, {z})",
                BitConverter.SingleToInt32Bits(TerrainIslands.ArchipelagoWeight(x, z))));
        }

        foreach (var cell in cells)
        {
            var exists = TerrainIslands.TryGetArchipelago(cell.X, cell.Z, out var centerX, out var centerZ,
                out var halfLength, out var halfWidth, out var direction);
            samples.Add(($"archipelago exists in cell {cell}", exists ? 1 : 0));
            samples.Add(($"archipelago center x in cell {cell}", BitConverter.SingleToInt32Bits(centerX)));
            samples.Add(($"archipelago center z in cell {cell}", BitConverter.SingleToInt32Bits(centerZ)));
            samples.Add(($"archipelago half length in cell {cell}", BitConverter.SingleToInt32Bits(halfLength)));
            samples.Add(($"archipelago half width in cell {cell}", BitConverter.SingleToInt32Bits(halfWidth)));
            samples.Add(($"archipelago direction x in cell {cell}", BitConverter.SingleToInt32Bits(direction.X)));
        }

        return samples;
    }

    [Fact]
    public void TheIslandTermIsAPureFunctionOfPositionAndSeed()
    {
        Assert.False(WorldSeed.WorldIsLive);
        try
        {
            // The sample points are fixed under the default seed and re-read under the other one
            var points = IslandPoints();
            var cells = ArchipelagoCells();
            var centers = new List<(float X, float Z)>();
            foreach (var cell in cells)
            {
                TerrainIslands.TryGetArchipelago(cell.X, cell.Z, out var centerX, out var centerZ, out _, out _, out _);
                centers.Add((centerX, centerZ));
            }

            // Positive controls: deep water past the clearing, and some archipelago footprints, to sample
            Assert.NotEmpty(points);
            Assert.NotEmpty(cells);
            Assert.Contains(points, p => TerrainIslands.LandWeight(p.X, p.Z, 1f) > 0f);

            var first = SampleIslands(points, cells, centers);
            WorldSeed.Apply(WorldSeed.Default + 7);
            var other = SampleIslands(points, cells, centers);
            WorldSeed.Apply(WorldSeed.Default);
            var again = SampleIslands(points, cells, centers);

            for (var i = 0; i < first.Count; i++)
                Assert.True(first[i].Bits == again[i].Bits, $"the {first[i].Term} differs after swapping the seed back");
            Assert.False(first.Select(s => s.Bits).SequenceEqual(other.Select(s => s.Bits)),
                "the island term did not change with the seed");
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    [Fact]
    public void TheSiteCachesFollowTheSeed()
    {
        Assert.False(WorldSeed.WorldIsLive);
        try
        {
            // Populates both caches under the default seed
            var first = SampleSites();

            WorldSeed.Apply(WorldSeed.Default + 7);
            AssertCachesMatchBuilders("after a seed swap");
            var other = SampleSites();
            Assert.False(first.Select(s => s.Bits).SequenceEqual(other.Select(s => s.Bits)),
                "no massif or lone-peak site moved with the seed");

            WorldSeed.Apply(WorldSeed.Default);
            AssertCachesMatchBuilders("after swapping the seed back");
            var again = SampleSites();
            for (var i = 0; i < first.Count; i++)
                Assert.True(first[i].Bits == again[i].Bits, $"the {first[i].Term} differs after swapping the seed back");
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }
}
