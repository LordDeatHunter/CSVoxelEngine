using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Ponds used to be dug from the land down to sea level with a fixed floor, so every one of them
/// was the same circular crater at the bottom of a 12-40 block pit. They now choose their own water
/// surface from the ground they stand on, which these tests pin: the surface is the pond's own, its
/// size and depth vary, and the banks enclose it.
/// </summary>
public sealed class LakeTests
{
    public LakeTests() => TestContent.EnsureLoaded();

    private const int ScanChunks = 40;

    private readonly record struct PondCell(int X, int Z, int Bed);

    // One shared scan of the world: every test below reads the same region, and building it costs
    // far more than the assertions do.
    private static readonly Lazy<Dictionary<int, List<PondCell>>> Ponds = new(() => ScanPonds());

    [Fact]
    public void PondsSitAtTheirOwnSurfaceAndNotAllAtOneLevel()
    {
        var ponds = Ponds.Value;
        Assert.True(ponds.Count >= 3, $"only {ponds.Count} pond surfaces in the scanned region");

        var above = 0;
        foreach (var (level, _) in ponds)
            if (level > WorldGenerationConfig.SeaLevel)
                above++;
        Assert.True(above >= 2, $"only {above} of {ponds.Count} pond surfaces sit above sea level");
    }

    [Fact]
    public void PondsVaryInSize()
    {
        var sizes = new List<int>();
        foreach (var (_, cells) in Ponds.Value)
            if (cells.Count >= 16)
                sizes.Add(cells.Count);
        Assert.True(sizes.Count >= 2, "need at least two ponds to compare sizes");

        sizes.Sort();
        Assert.True(sizes[^1] >= sizes[0] * 4,
            $"pond sizes are uniform: {sizes[0]} to {sizes[^1]} columns");
    }

    [Fact]
    public void PondsAreDugFromTheirOwnGroundNotDownToSeaLevel()
    {
        var aboveSeaLevel = 0;
        foreach (var (level, cells) in Ponds.Value)
        {
            if (level <= WorldGenerationConfig.SeaLevel) continue;
            foreach (var cell in cells)
                if (cell.Bed > WorldGenerationConfig.SeaLevel)
                    aboveSeaLevel++;
        }

        Assert.True(aboveSeaLevel > 0,
            "no pond floor sits above sea level: ponds are still dug down to the waterline");
    }

    [Fact]
    public void PondBanksStandAtOrAboveTheWater()
    {
        var leaks = 0;
        foreach (var (level, cells) in Ponds.Value)
        foreach (var cell in cells)
        for (var d = 0; d < 4; d++)
        {
            var nx = cell.X + (d == 0 ? 1 : d == 1 ? -1 : 0);
            var nz = cell.Z + (d == 2 ? 1 : d == 3 ? -1 : 0);
            var column = Chunk.GetColumnData(nx >> Chunk.SizeShift, nz >> Chunk.SizeShift);
            var local = ((nx & Chunk.SizeMask) << Chunk.SizeShift) | (nz & Chunk.SizeMask);
            var neighbourLevel = column.WaterLevel[local];
            if (neighbourLevel == level) continue;
            if (column.Bed[local] < neighbourLevel) continue;
            if (column.Heights[local] >= level) continue;
            leaks++;
        }

        Assert.Equal(0, leaks);
    }

    [Fact]
    public void AnElevatedPondIsFilledWithWaterUpToItsOwnSurface()
    {
        var checkedPond = false;
        foreach (var (level, cells) in Ponds.Value)
        {
            if (level <= WorldGenerationConfig.SeaLevel) continue;
            foreach (var cell in cells)
            {
                if (TerrainGenerator.ColdWeight(cell.X, cell.Z)
                    > WorldGenerationConfig.FreezeColdThreshold) continue;

                var cy = level >> Chunk.SizeShift;
                var bedLy = cell.Bed - cy * Chunk.Size;
                if (bedLy < 1) continue;

                var chunk = new Chunk(null!,
                    new Vector3(cell.X >> Chunk.SizeShift, cy, cell.Z >> Chunk.SizeShift),
                    generateBlockData: true);
                var lx = cell.X & Chunk.SizeMask;
                var lz = cell.Z & Chunk.SizeMask;

                Assert.Equal(TerrainGenerator.SeaBlock,
                    chunk.GetLocalBlock(lx, level - cy * Chunk.Size, lz));
                Assert.NotEqual(TerrainGenerator.SeaBlock, chunk.GetLocalBlock(lx, bedLy, lz));
                checkedPond = true;
                break;
            }

            if (checkedPond) break;
        }

        Assert.True(checkedPond, "no elevated pond column found to check the fill at");
    }

    private static Dictionary<int, List<PondCell>> ScanPonds()
    {
        var ponds = new Dictionary<int, List<PondCell>>();

        for (var cx = -ScanChunks; cx <= ScanChunks; cx++)
        for (var cz = -ScanChunks; cz <= ScanChunks; cz++)
        {
            var column = Chunk.GetColumnData(cx, cz);
            for (var i = 0; i < column.Heights.Length; i++)
            {
                var level = column.WaterLevel[i];
                if (level == TerrainGenerator.NoWater || column.Bed[i] >= level) continue;
                if (column.Biomes[i] == Biome.Ocean) continue;

                var x = cx * Chunk.Size + (i >> Chunk.SizeShift);
                var z = cz * Chunk.Size + (i & Chunk.SizeMask);
                if (!ponds.TryGetValue(level, out var cells)) ponds[level] = cells = [];
                cells.Add(new PondCell(x, z, column.Bed[i]));
            }
        }

        return ponds;
    }
}
