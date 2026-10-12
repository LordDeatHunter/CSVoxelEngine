using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>
/// Geodes and meteor craters: a geode carves a crystal shell around an air heart deep in stone,
/// and a crater cuts an obsidian-floored bowl into the land.
/// </summary>
public sealed class GeodeMeteorTests
{
    public GeodeMeteorTests() => TestContent.EnsureLoaded();

    [Fact]
    public void AGeodeCarvesACrystalShellAroundAir()
    {
        // A real center from the pure hash, clear of its chunk's borders so nothing clips
        var found = false;
        var centerX = 0;
        var centerY = 0;
        var centerZ = 0;
        var radius = 0;
        for (var cellX = -80; cellX <= 80 && !found; cellX++)
        for (var cellZ = -80; cellZ <= 80 && !found; cellZ++)
        {
            if (!GeodeGenerator.TryGetGeode(cellX, cellZ, out var x, out var y, out var z, out var r)) continue;
            if ((x & Chunk.SizeMask) < r + 1 || (x & Chunk.SizeMask) > Chunk.Size - r - 2) continue;
            if ((z & Chunk.SizeMask) < r + 1 || (z & Chunk.SizeMask) > Chunk.Size - r - 2) continue;
            if ((y & Chunk.SizeMask) < r + 1 || (y & Chunk.SizeMask) > Chunk.Size - r - 2) continue;
            centerX = x;
            centerY = y;
            centerZ = z;
            radius = r;
            found = true;
        }
        Assert.True(found, "no geode candidate clear of a chunk border");

        var worldX0 = centerX & ~Chunk.SizeMask;
        var worldY0 = centerY & ~Chunk.SizeMask;
        var worldZ0 = centerZ & ~Chunk.SizeMask;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        for (var i = 0; i < blocks.Length; i++) blocks[i] = stone;

        GeodeGenerator.Stamp(blocks, worldX0, worldY0, worldZ0);

        var lx = centerX - worldX0;
        var ly = centerY - worldY0;
        var lz = centerZ - worldZ0;
        Assert.Equal(BlockType.Air, blocks[Chunk.Idx(lx, ly, lz)]);
        Assert.Equal(GeodeGenerator.CrystalBlock, blocks[Chunk.Idx(lx + radius, ly, lz)]);
        Assert.Equal(stone, blocks[Chunk.Idx(lx + radius + 1, ly, lz)]);
    }

    [Fact]
    public void ACraterCutsAnObsidianFlooredBowl()
    {
        // A real center from the pure hash, clear of its chunk's borders so nothing clips
        var found = false;
        var centerX = 0;
        var centerZ = 0;
        var radius = 0;
        for (var cellX = -80; cellX <= 80 && !found; cellX++)
        for (var cellZ = -80; cellZ <= 80 && !found; cellZ++)
        {
            if (!MeteorGenerator.TryGetCrater(cellX, cellZ, out var x, out var z, out var r)) continue;
            if ((x & Chunk.SizeMask) < r + 1 || (x & Chunk.SizeMask) > Chunk.Size - r - 2) continue;
            if ((z & Chunk.SizeMask) < r + 1 || (z & Chunk.SizeMask) > Chunk.Size - r - 2) continue;
            centerX = x;
            centerZ = z;
            radius = r;
            found = true;
        }
        Assert.True(found, "no crater candidate clear of a chunk border");

        var worldX0 = centerX & ~Chunk.SizeMask;
        var worldZ0 = centerZ & ~Chunk.SizeMask;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var heights = new int[Chunk.Size * Chunk.Size];
        for (var i = 0; i < heights.Length; i++) heights[i] = 20;
        var column = new ColumnData(heights, new byte[heights.Length], new float[heights.Length],
            new float[heights.Length]);

        // A chunk based at y=16: stone up to the surface at y=20, air above
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        for (var y = 0; y <= 4; y++)
            blocks[Chunk.Idx(x, y, z)] = stone;

        MeteorGenerator.Stamp(blocks, column, worldX0, 16, worldZ0);

        // The center is cut deepest and floored with obsidian; the rim is barely touched
        var lx = centerX - worldX0;
        var lz = centerZ - worldZ0;
        var depth = (int)(radius * WorldGenerationConfig.MeteorBowlSlope);
        Assert.Equal(MeteorGenerator.ObsidianBlock, blocks[Chunk.Idx(lx, 4 - depth + 1, lz)]);
        Assert.Equal(BlockType.Air, blocks[Chunk.Idx(lx, 4, lz)]);
        Assert.Equal(stone, blocks[Chunk.Idx(lx + radius + 1, 4, lz)]);
    }
}
