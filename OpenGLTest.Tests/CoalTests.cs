using System;
using System.Collections.Generic;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class CoalTests
{
    public CoalTests() => TestContent.EnsureLoaded();

    [Fact]
    public void VeinsExistAtAnyDepth()
    {
        var shallow = false;
        var deep = false;
        for (var x = 0; x < 128 && !(shallow && deep); x++)
        for (var z = 0; z < 128 && !(shallow && deep); z++)
        for (var y = 0; y < 16 && !(shallow && deep); y++)
        {
            if (!shallow && CoalGenerator.HasOre(x, 8 + y, z)) shallow = true;
            if (!deep && CoalGenerator.HasOre(x, -64 + y, z)) deep = true;
        }
        Assert.True(shallow, "No coal found near the surface in a 128x128 scan.");
        Assert.True(deep, "No coal found deep down in a 128x128 scan.");
    }

    [Fact]
    public void StampReplacesStoneAndSandstoneButLeavesOtherHostsAlone()
    {
        var ore = ContentRegistry.RequireBlock("coal_ore").Id;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var sandstone = ContentRegistry.RequireBlock("sandstone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        var water = ContentRegistry.RequireBlock("water").Id;

        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        for (var i = 0; i < blocks.Length; i++) blocks[i] = (i & 1) == 0 ? stone : sandstone;
        blocks[Chunk.Idx(1, 1, 1)] = dirt;
        blocks[Chunk.Idx(2, 2, 2)] = water;
        blocks[Chunk.Idx(3, 3, 3)] = BlockType.Air;

        CoalGenerator.Stamp(blocks, 0, 0, 0);

        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var got = blocks[Chunk.Idx(x, y, z)];
            if (x == 1 && y == 1 && z == 1) { Assert.Equal(dirt, got); continue; }
            if (x == 2 && y == 2 && z == 2) { Assert.Equal(water, got); continue; }
            if (x == 3 && y == 3 && z == 3) { Assert.Equal(BlockType.Air, got); continue; }
            var host = (Chunk.Idx(x, y, z) & 1) == 0 ? stone : sandstone;
            Assert.Equal(CoalGenerator.HasOre(x, y, z) ? ore : host, got);
        }
    }

    [Fact]
    public void CoalVeinsAreLargerThanMalachiteVeins()
    {
        var malachiteMean = MeanDepositSize(MalachiteGenerator.HasOre);
        var coalMean = MeanDepositSize(CoalGenerator.HasOre);

        Assert.True(malachiteMean > 0 && coalMean > 0, "a generator produced no deposits");
        Assert.True(coalMean > malachiteMean,
            $"coal veins should be larger than malachite's (coal {coalMean:F1}, malachite {malachiteMean:F1})");
        Assert.True(coalMean < malachiteMean * 2,
            $"coal veins should be only slightly larger (coal {coalMean:F1}, malachite {malachiteMean:F1})");
    }

    private static double MeanDepositSize(Func<int, int, int, bool> hasOre)
    {
        const int x0 = 0;
        const int y0 = -64;
        const int z0 = 0;
        const int sx = 128;
        const int sy = 96;
        const int sz = 128;

        var cells = new HashSet<(int, int, int)>();
        for (var x = 0; x < sx; x++)
        for (var y = 0; y < sy; y++)
        for (var z = 0; z < sz; z++)
            if (hasOre(x0 + x, y0 + y, z0 + z))
                cells.Add((x, y, z));

        if (cells.Count == 0) return 0;

        var visited = new HashSet<(int, int, int)>();
        var work = new Stack<(int, int, int)>();
        var deposits = 0;
        var total = 0;
        foreach (var cell in cells)
        {
            if (!visited.Add(cell)) continue;
            deposits++;
            work.Push(cell);
            while (work.Count > 0)
            {
                var (cx, cy, cz) = work.Pop();
                total++;
                foreach (var d in Offsets)
                {
                    var n = (cx + d.Item1, cy + d.Item2, cz + d.Item3);
                    if (cells.Contains(n) && visited.Add(n)) work.Push(n);
                }
            }
        }
        return (double)total / deposits;
    }

    private static readonly (int, int, int)[] Offsets =
    {
        (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1),
    };
}
