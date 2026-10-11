using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Snow on canopies: a cold column's highest leaf with air over it wears a snow cell, while the
/// ground snow it already laid is untouched.
/// </summary>
public sealed class SnowCanopyTests
{
    public SnowCanopyTests() => TestContent.EnsureLoaded();

    [Fact]
    public void AColdColumnsTopLeafWearsSnow()
    {
        // A cold column from the real climate noise (the stamp reads ColdWeight itself)
        var worldX = 0;
        var worldZ = 0;
        var found = false;
        for (var x = -3000; x <= 3000 && !found; x += 23)
        for (var z = -3000; z <= 3000 && !found; z += 23)
        {
            if (SnowGenerator.DepthAt(x, z) <= 0) continue;
            worldX = x;
            worldZ = z;
            found = true;
        }
        Assert.True(found, "no cold column found");

        // A hand-built chunk: a solid floor at y=10, a spruce leaf crown at y=14, air above
        var heights = new int[Chunk.Size * Chunk.Size];
        var water = new int[Chunk.Size * Chunk.Size];
        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] = 10;
            water[i] = TerrainGenerator.NoWater;
        }
        var column = new ColumnData(heights, new byte[heights.Length], new float[heights.Length],
            new float[heights.Length], waterLevel: water);

        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var leaves = ContentRegistry.RequireBlock("spruce_leaves").Id;
        var worldX0 = worldX - (worldX & Chunk.SizeMask);
        var worldZ0 = worldZ - (worldZ & Chunk.SizeMask);
        var lx = worldX & Chunk.SizeMask;
        var lz = worldZ & Chunk.SizeMask;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            blocks[Chunk.Idx(x, 10, z)] = stone;
        blocks[Chunk.Idx(lx, 14, lz)] = leaves;

        SnowGenerator.Stamp(blocks, column, worldX0, 0, worldZ0);

        var snow = TerrainGenerator.SnowBlock;
        // The leaf crown wears snow, and the ground under it keeps its own layer
        Assert.Equal(snow, blocks[Chunk.Idx(lx, 15, lz)]);
        Assert.Equal(snow, blocks[Chunk.Idx(lx, 11, lz)]);
        // A neighbour column with no crown takes only the ground layer
        var otherX = (lx + 1) & Chunk.SizeMask;
        Assert.Equal(snow, blocks[Chunk.Idx(otherX, 11, lz)]);
        Assert.Equal(BlockType.Air, blocks[Chunk.Idx(otherX, 12, lz)]);
    }
}
