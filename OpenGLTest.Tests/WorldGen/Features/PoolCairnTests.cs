using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>
/// Underground pools and cairns: a pool ponds water only on a cave floor between the lava band and
/// the sea, and a cairn writes its three-cell stack with a pebble hat into air only.
/// </summary>
public sealed class PoolCairnTests
{
    public PoolCairnTests() => TestContent.EnsureLoaded();

    [Fact]
    public void PoolsPondOnlyBetweenTheLavaAndTheSeaOnFloors()
    {
        var water = UndergroundPoolGenerator.WaterBlock;
        var stone = ContentRegistry.RequireBlock("stone").Id;

        // A real cell the pool rule fires at: the patch mask is the climate-like noise itself, so
        // the test scans for one instead of hoping its own chunk sits inside a patch
        var found = false;
        var worldX = 0;
        var worldY = 0;
        var worldZ = 0;
        for (var x = 0; x <= 4000 && !found; x += 3)
        for (var z = 0; z <= 4000 && !found; z += 3)
        for (var y = -16; y <= -2 && !found; y++)
        {
            if (!UndergroundPoolGenerator.HasPool(x, y, z, 40)) continue;
            worldX = x;
            worldY = y;
            worldZ = z;
            found = true;
        }
        Assert.True(found, "the pool rule never fires in the scanned area");

        // A hand-built column: the surface at y=40 and a cave room under the sea, its floor at
        // y=-15 (the bottom layer of the chunk at y=-16, so the floor is visible to the stamp)
        var heights = new int[Chunk.Size * Chunk.Size];
        var levels = new int[Chunk.Size * Chunk.Size];
        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] = 40;
            levels[i] = TerrainGenerator.NoWater;
        }
        var column = new ColumnData(heights, new byte[heights.Length], new float[heights.Length],
            new float[heights.Length], waterLevel: levels);

        var worldX0 = worldX & ~Chunk.SizeMask;
        var worldZ0 = worldZ & ~Chunk.SizeMask;
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            blocks[Chunk.Idx(x, 1, z)] = stone;

        UndergroundPoolGenerator.Stamp(blocks, column, worldX0, -16, worldZ0);

        // The cell the rule fired at holds water, and every pool cell obeys the band, the floor
        // and the single-cell rules
        Assert.Equal(water, blocks[Chunk.Idx(worldX - worldX0, worldY + 16, worldZ - worldZ0)]);
        var ponds = 0;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            for (var ly = 2; ly < Chunk.Size; ly++)
            {
                if (blocks[Chunk.Idx(x, ly, z)] != water) continue;
                var y = -16 + ly;
                ponds++;
                Assert.InRange(y, WorldGenerationConfig.LavaMaxY + 1, WorldGenerationConfig.SeaLevel);
                Assert.NotEqual(BlockType.Air, blocks[Chunk.Idx(x, ly - 1, z)]);
                Assert.True(UndergroundPoolGenerator.HasPool(worldX0 + x, y, worldZ0 + z, 40),
                    $"water at ({worldX0 + x}, {y}, {worldZ0 + z}) is outside every pool");
                Assert.NotEqual(water, blocks[Chunk.Idx(x, ly + 1, z)]);
            }
        }
        Assert.True(ponds > 0);
    }

    [Fact]
    public void ACairnWritesItsStackAndHatIntoAirOnly()
    {
        var cobble = CairnGenerator.CobblestoneBlock;
        var pebble = CairnGenerator.PebbleBlock;
        var stone = ContentRegistry.RequireBlock("stone").Id;

        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        // The chunk holding the ground at y=50, and a stone in the hat's cell so it is left alone
        blocks[Chunk.Idx(3, 2, 4)] = stone;
        blocks[Chunk.Idx(3, 6, 4)] = stone;
        CairnGenerator.StampStack(blocks, 48, 3, 4, 50);

        Assert.Equal(cobble, blocks[Chunk.Idx(3, 3, 4)]);
        Assert.Equal(cobble, blocks[Chunk.Idx(3, 4, 4)]);
        Assert.Equal(cobble, blocks[Chunk.Idx(3, 5, 4)]);
        // The hat cell was occupied, so it kept its stone
        Assert.Equal(stone, blocks[Chunk.Idx(3, 6, 4)]);
    }

    [Fact]
    public void TheCairnHashIsDeterministicAndInsideItsCell()
    {
        var candidates = 0;
        for (var cellX = -20; cellX <= 20; cellX++)
        for (var cellZ = -20; cellZ <= 20; cellZ++)
        {
            var first = CairnGenerator.TryGetCairn(cellX, cellZ, out var x, out var z);
            Assert.Equal(first, CairnGenerator.TryGetCairn(cellX, cellZ, out var x2, out var z2));
            Assert.Equal(x, x2);
            Assert.Equal(z, z2);
            if (!first) continue;
            candidates++;
            Assert.InRange(x, cellX << WorldGenerationConfig.CairnCellShift,
                ((cellX + 1) << WorldGenerationConfig.CairnCellShift) - 1);
            Assert.InRange(z, cellZ << WorldGenerationConfig.CairnCellShift,
                ((cellZ + 1) << WorldGenerationConfig.CairnCellShift) - 1);
        }
        Assert.True(candidates > 0, "no cairn candidate in the sampled cells");
    }
}
