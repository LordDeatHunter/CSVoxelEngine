using System.Collections.Generic;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class MalachiteTests
{
    public MalachiteTests() => TestContent.EnsureLoaded();

    [Fact]
    public void VeinsExistAtAnyDepth()
    {
        var shallow = false;
        var deep = false;
        for (var x = 0; x < 128 && !(shallow && deep); x++)
        for (var z = 0; z < 128 && !(shallow && deep); z++)
        for (var y = 0; y < 16 && !(shallow && deep); y++)
        {
            if (!shallow && MalachiteGenerator.HasOre(x, 8 + y, z)) shallow = true;
            if (!deep && MalachiteGenerator.HasOre(x, -64 + y, z)) deep = true;
        }
        Assert.True(shallow, "No malachite found near the surface in a 128x128 scan.");
        Assert.True(deep, "No malachite found deep down in a 128x128 scan.");
    }

    [Fact]
    public void StampOnlyReplacesStone()
    {
        var ore = ContentRegistry.RequireBlock("malachite_ore").Id;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        var water = ContentRegistry.RequireBlock("water").Id;

        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        for (var i = 0; i < blocks.Length; i++) blocks[i] = stone;
        blocks[Chunk.Idx(1, 1, 1)] = dirt;
        blocks[Chunk.Idx(2, 2, 2)] = water;
        blocks[Chunk.Idx(3, 3, 3)] = BlockType.Air;

        MalachiteGenerator.Stamp(blocks, 0, 0, 0);

        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var got = blocks[Chunk.Idx(x, y, z)];
            if ((x == 1 && y == 1 && z == 1)) { Assert.Equal(dirt, got); continue; }
            if ((x == 2 && y == 2 && z == 2)) { Assert.Equal(water, got); continue; }
            if ((x == 3 && y == 3 && z == 3)) { Assert.Equal(BlockType.Air, got); continue; }
            Assert.Equal(MalachiteGenerator.HasOre(x, y, z) ? ore : stone, got);
        }
    }

    [Fact]
    public void DepositsAverageAroundFiftyOresAndStaySparse()
    {
        const int x0 = 0;
        const int y0 = -64;
        const int z0 = 0;
        const int sx = 128;
        const int sy = 96;
        const int sz = 128;

        var oreCells = new HashSet<(int, int, int)>();
        for (var x = 0; x < sx; x++)
        for (var y = 0; y < sy; y++)
        for (var z = 0; z < sz; z++)
            if (MalachiteGenerator.HasOre(x0 + x, y0 + y, z0 + z))
                oreCells.Add((x, y, z));

        Assert.NotEmpty(oreCells);

        var visited = new HashSet<(int, int, int)>();
        var sizes = new List<int>();
        var work = new Stack<(int, int, int)>();
        foreach (var cell in oreCells)
        {
            if (!visited.Add(cell)) continue;
            var size = 0;
            work.Push(cell);
            while (work.Count > 0)
            {
                var (cx, cy, cz) = work.Pop();
                size++;
                foreach (var d in Offsets)
                {
                    var n = (cx + d.Item1, cy + d.Item2, cz + d.Item3);
                    if (oreCells.Contains(n) && visited.Add(n)) work.Push(n);
                }
            }
            sizes.Add(size);
        }

        sizes.Sort();
        var total = 0;
        foreach (var s in sizes) total += s;
        var mean = (double)total / sizes.Count;
        var median = sizes[sizes.Count / 2];

        Assert.True(mean >= 40 && mean <= 60,
            $"Mean deposit size {mean:F1} (median {median}, {sizes.Count} deposits, {total} ores). Expected ~50.");
        // Common as coal: most chunk columns hold several deposits, so casual mining
        // and cave walls run into ore without a dedicated hunt. Either direction is a
        // regression: too few means it goes back to being unfindable, far more means
        // the field is percolating into ore caverns.
        Assert.True(sizes.Count >= 300 && sizes.Count <= 550,
            $"Found {sizes.Count} deposits in a 128x96x128 volume. Expected a few hundred.");
    }

    private static readonly (int, int, int)[] Offsets =
    {
        (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1),
    };
}
