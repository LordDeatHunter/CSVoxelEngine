using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.WorldGen.Features.Underground;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features.Underground;

/// <summary>
/// Hematite (iron) veins: deep stone only, never above <see cref="WorldGenerationConfig.HematiteMaxY"/>
/// (in the point query and the stamp alike), rarer than malachite in their own band, and a pure
/// function of the world seed. In the "Worldgen seed" collection because one fact reseeds.
/// </summary>
[Collection("Worldgen seed")]
public sealed class HematiteTests
{
    private const int MaxY = WorldGenerationConfig.HematiteMaxY;

    public HematiteTests() => TestContent.EnsureLoaded();

    [Fact]
    public void VeinsExistBelowTheCeilingAndNoneAbove()
    {
        var below = false;
        for (var x = 0; x < 128 && !below; x++)
        for (var z = 0; z < 128 && !below; z++)
        for (var y = MaxY - 15; y <= MaxY && !below; y++)
        {
            if (HematiteGenerator.HasOre(x, y, z)) below = true;
        }
        Assert.True(below, "No hematite found in the band just under the ceiling in a 128x128 scan.");

        for (var x = 0; x < 128; x++)
        for (var z = 0; z < 128; z++)
        for (var y = MaxY + 1; y <= MaxY + 48; y++)
        {
            Assert.False(HematiteGenerator.HasOre(x, y, z), $"hematite above the ceiling at ({x}, {y}, {z})");
        }

        // A stone chunk the ceiling runs through: nothing above it may change
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var y0 = (MaxY >> 4) << 4;
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        for (var x0 = 0; x0 < 128; x0 += Chunk.Size)
        for (var z0 = 0; z0 < 128; z0 += Chunk.Size)
        {
            Array.Fill(blocks, stone);
            HematiteGenerator.Stamp(blocks, x0, y0, z0);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = MaxY - y0 + 1; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                Assert.True(blocks[Chunk.Idx(x, y, z)] == stone,
                    $"the stamp wrote above the ceiling at ({x0 + x}, {y0 + y}, {z0 + z})");
            }
        }
    }

    [Fact]
    public void StampOnlyReplacesStone()
    {
        var ore = HematiteGenerator.OreBlock;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var sandstone = ContentRegistry.RequireBlock("sandstone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        var water = ContentRegistry.RequireBlock("water").Id;

        // A row of chunks wholly below the ceiling, so a rare ore still turns up somewhere
        var y0 = ((MaxY >> 4) << 4) - Chunk.Size;
        var oreCells = 0;
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        for (var x0 = 0; x0 < 128; x0 += Chunk.Size)
        for (var z0 = 0; z0 < 128; z0 += Chunk.Size)
        {
            for (var i = 0; i < blocks.Length; i++) blocks[i] = (i & 1) == 0 ? stone : sandstone;
            blocks[Chunk.Idx(1, 1, 1)] = dirt;
            blocks[Chunk.Idx(2, 2, 2)] = water;
            blocks[Chunk.Idx(3, 3, 3)] = BlockType.Air;

            HematiteGenerator.Stamp(blocks, x0, y0, z0);

            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var i = Chunk.Idx(x, y, z);
                var got = blocks[i];
                if (x == 1 && y == 1 && z == 1) { Assert.Equal(dirt, got); continue; }
                if (x == 2 && y == 2 && z == 2) { Assert.Equal(water, got); continue; }
                if (x == 3 && y == 3 && z == 3) { Assert.Equal(BlockType.Air, got); continue; }
                if ((i & 1) != 0) { Assert.Equal(sandstone, got); continue; }
                var expected = HematiteGenerator.HasOre(x0 + x, y0 + y, z0 + z) ? ore : stone;
                Assert.Equal(expected, got);
                if (got == ore) oreCells++;
            }
        }

        Assert.True(oreCells > 0, "no hematite stamped, so the host check proves nothing");
    }

    [Fact]
    public void HematiteIsRarerThanMalachiteInItsOwnBand()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var hematite = OreScan.StampedCells(HematiteGenerator.Stamp, stone, HematiteGenerator.OreBlock);
        var malachite = OreScan.StampedCells(MalachiteGenerator.Stamp, stone, MalachiteGenerator.OreBlock)
            .Where(c => c.y <= MaxY)
            .ToList();

        Assert.True(hematite.Count > 0, "no hematite in the scan volume");
        Assert.All(hematite, c => Assert.True(c.y <= MaxY, $"hematite above the ceiling at {c}"));
        Assert.True(hematite.Count < malachite.Count,
            $"hematite should be rarer than malachite at or below y {MaxY} " +
            $"(hematite {hematite.Count} cells, malachite {malachite.Count})");
    }

    [Fact]
    public void TheVeinsFollowTheWorldSeed()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
            var first = OreSet();

            WorldSeed.Apply(WorldSeed.Default + 7);
            var other = OreSet();
            Assert.False(first.SetEquals(other), "another seed laid exactly the same hematite");

            WorldSeed.Apply(WorldSeed.Default);
            var again = OreSet();
            Assert.True(first.SetEquals(again), "the same seed laid different hematite");
            Assert.NotEmpty(first);
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }

    /// <summary>Every hematite cell of the point query in the band just under the ceiling.</summary>
    private static HashSet<(int x, int y, int z)> OreSet()
    {
        var cells = new HashSet<(int x, int y, int z)>();
        for (var x = 0; x < 64; x++)
        for (var z = 0; z < 64; z++)
        for (var y = MaxY - 31; y <= MaxY; y++)
        {
            if (HematiteGenerator.HasOre(x, y, z)) cells.Add((x, y, z));
        }
        return cells;
    }
}
