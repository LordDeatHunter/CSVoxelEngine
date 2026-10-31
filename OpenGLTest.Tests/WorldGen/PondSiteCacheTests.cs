using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// The pond verdict cache (<see cref="WaterBodies.PondSiteAt"/>) is a pure memo: a cached verdict is
/// exactly the uncached one (<see cref="WaterBodies.BuildPondSite"/>), the pond bowl it feeds is bit for
/// bit the bowl an uncached sampler computes, and a seed swap drops every verdict computed under the
/// previous seed.
/// </summary>
[Collection("Worldgen seed")]
public sealed class PondSiteCacheTests
{
    public PondSiteCacheTests() => TestContent.EnsureLoaded();

    // Small enough that every cell fits the cache at once, so the second pass is served from it
    private const int CellHalfExtent = 30;

    /// <summary>The lake cells around the origin whose candidate <see cref="WaterBodies.LakeSample"/>
    /// would ask about: every cell except a rolled one inside the spawn clearing.</summary>
    private static List<(int X, int Z)> Cells()
    {
        var cells = new List<(int X, int Z)>();
        for (var gx = -CellHalfExtent; gx <= CellHalfExtent; gx++)
        for (var gz = -CellHalfExtent; gz <= CellHalfExtent; gz++)
        {
            if (Roll(gx, gz, out var centerX, out var centerZ, out _)
                && MathF.Abs(centerX) < WorldGenerationConfig.LakeSpawnClearRadius
                && MathF.Abs(centerZ) < WorldGenerationConfig.LakeSpawnClearRadius)
                continue;
            cells.Add((gx, gz));
        }

        return cells;
    }

    private static bool Roll(int gx, int gz, out float centerX, out float centerZ, out float size) =>
        TerrainSites.TryRoll(gx, gz, WorldGenerationConfig.LakeCellBlocks, WaterBodies.LakeSalt,
            WorldGenerationConfig.LakeChance, WorldGenerationConfig.LakeRadiusSkew, out _, out centerX,
            out centerZ, out size);

    [Fact]
    public void TheCachedPondVerdictMatchesTheUncachedOne()
    {
        var cells = Cells();
        var accepted = 0;
        var rejected = 0;
        foreach (var (gx, gz) in cells)
        {
            var uncached = WaterBodies.BuildPondSite((gx, gz));
            Assert.Equal(uncached, WaterBodies.PondSiteAt(gx, gz));
            if (!Roll(gx, gz, out _, out _, out _)) continue;
            if (uncached.Accepted) accepted++;
            else rejected++;
        }

        // A second pass reads the verdicts back from the cache
        foreach (var (gx, gz) in cells)
            Assert.Equal(WaterBodies.BuildPondSite((gx, gz)), WaterBodies.PondSiteAt(gx, gz));

        Assert.True(accepted > 0, "no rolled cell held a pond, so the cache was never asked for one");
        Assert.True(rejected > 0, "no rolled cell was refused, so a refused verdict was never cached");
    }

    [Fact]
    public void LakeSampleMatchesAnUncachedReimplementation()
    {
        var columns = new List<(int X, int Z)>();
        var pondChunks = 0;
        for (var cx = -800; cx <= 800 && pondChunks < 6; cx += 7)
        for (var cz = -800; cz <= 800 && pondChunks < 6; cz += 7)
        {
            if (Math.Abs(cx) < 10 && Math.Abs(cz) < 10) continue;
            if (WaterBodies.LakeSample(cx * 16 + 8, cz * 16 + 8, out _, out _) <= 0f) continue;

            pondChunks++;
            for (var x = 0; x < 16; x++)
            for (var z = 0; z < 16; z++)
                columns.Add((cx * 16 + x, cz * 16 + z));
        }

        for (var x = -6000; x <= 6000; x += 97)
        for (var z = -6000; z <= 6000; z += 97)
            columns.Add((x, z));

        var wet = 0;
        foreach (var (x, z) in columns)
        {
            var expected = UncachedLakeSample(x, z, out var expectedSurface, out var expectedFloor);
            var actual = WaterBodies.LakeSample(x, z, out var surface, out var floor);
            Assert.True(BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual),
                $"pond weight at ({x}, {z}): cached {actual}, uncached {expected}");
            Assert.Equal(expectedSurface, surface);
            Assert.Equal(expectedFloor, floor);
            if (actual > 0f) wet++;
        }

        Assert.True(wet > 0, "no sampled column lay in a pond, so the cached verdict was never read");
    }

    [Fact]
    public void ASeedChangeResetsTheVerdicts()
    {
        var cells = Cells();
        try
        {
            // Warms the cache with the default seed's verdicts
            var first = cells.Select(c => WaterBodies.PondSiteAt(c.X, c.Z)).ToList();

            WorldSeed.Apply(WorldSeed.Default + 7);
            var other = new List<(bool Accepted, int Surface, int Floor)>();
            foreach (var (gx, gz) in cells)
            {
                var uncached = WaterBodies.BuildPondSite((gx, gz));
                Assert.Equal(uncached, WaterBodies.PondSiteAt(gx, gz));
                other.Add(uncached);
            }

            // Otherwise a stale verdict would pass the check above unnoticed
            Assert.False(first.SequenceEqual(other), "no pond verdict moved with the seed");
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    /// <summary>
    /// <see cref="WaterBodies.LakeSample"/> written out again, asking <see cref="WaterBodies.BuildPondSite"/>
    /// directly instead of the cache.
    /// </summary>
    private static float UncachedLakeSample(float x, float z, out int surface, out int floor)
    {
        const int cell = WorldGenerationConfig.LakeCellBlocks;
        TerrainSites.CellRange(x, cell, WorldGenerationConfig.LakeMaxRadius, out var gxMin, out var gxMax);
        TerrainSites.CellRange(z, cell, WorldGenerationConfig.LakeMaxRadius, out var gzMin, out var gzMax);
        var best = 0f;
        surface = WaterBodies.NoWater;
        floor = 0;

        for (var gx = gxMin; gx <= gxMax; gx++)
        for (var gz = gzMin; gz <= gzMax; gz++)
        {
            if (!Roll(gx, gz, out var centerX, out var centerZ, out var size)) continue;
            if (MathF.Abs(centerX) < WorldGenerationConfig.LakeSpawnClearRadius
                && MathF.Abs(centerZ) < WorldGenerationConfig.LakeSpawnClearRadius)
                continue;

            var nominal = MathUtils.Lerp(WorldGenerationConfig.LakeMinRadius, WorldGenerationConfig.LakeMaxRadius,
                size);
            var ox = x - centerX;
            var oz = z - centerZ;
            var d2 = ox * ox + oz * oz;
            if (d2 >= nominal * nominal) continue;

            var site = WaterBodies.BuildPondSite((gx, gz));
            if (!site.Accepted) continue;

            var radius = TerrainSites.Wobble(nominal, WorldGenerationConfig.LakeShapeAmplitude,
                TerrainNoise.LakeShapeNoise, x, z);
            if (d2 >= radius * radius) continue;

            var w = MathUtils.SmoothStep(1f - MathF.Sqrt(d2) / radius);
            if (w > best)
            {
                best = w;
                surface = site.Surface;
                floor = site.Floor;
            }
        }

        return best;
    }
}
