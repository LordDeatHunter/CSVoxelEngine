using System.Collections.Generic;
using VoxelEngine.WorldGen.Features.Underground;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features.Underground;

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
        var ore = ContentRegistry.RequireBlock("malachite_ore").Id;
        var stone = ContentRegistry.RequireBlock("stone").Id;

        var oreCells = OreScan.StampedCells(MalachiteGenerator.Stamp, stone, ore);

        Assert.NotEmpty(oreCells);

        var deposits = OreScan.Deposits(oreCells);
        var sizes = new List<int>();
        foreach (var deposit in deposits) sizes.Add(deposit.Count);

        sizes.Sort();
        var total = oreCells.Count;
        var mean = OreScan.MeanSize(deposits);
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
}
