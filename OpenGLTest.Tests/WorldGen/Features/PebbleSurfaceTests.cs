using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Rock piles are surface-gated: an exposed block's <c>pebbleWeight</c> scales the scatter, so
/// stone and gravel keep the patch's full density, grass gets a thinner share and sand none.
/// These tests assert that ordering and the exclusion, not the tuned numbers.
/// </summary>
public sealed class PebbleSurfaceTests
{
    public PebbleSurfaceTests() => TestContent.EnsureLoaded();

    private static float Weight(string block) =>
        BlockType.PebbleWeightOf(ContentRegistry.RequireBlock(block).Id);

    [Fact]
    public void RockySurfacesHostPilesGrassThinsThemAndSandNeverDoes()
    {
        var stone = Weight("stone");
        var gravel = Weight("gravel");
        var grass = Weight("grass");

        Assert.True(stone > 0f, "stone must host rock piles");
        Assert.True(gravel > 0f, "gravel must host rock piles");
        Assert.True(grass > 0f, "grass should still grow the occasional pile");
        Assert.True(grass < stone && grass < gravel, "grass is the occasional host, not the main one");
        Assert.Equal(0f, Weight("sand"));
        Assert.Equal(0f, Weight("sandstone"));
    }

    [Fact]
    public void ASurfaceWeightOnlyThinsTheSamePatchNeverWidensIt()
    {
        for (var x = -64; x <= 64; x += 7)
        for (var z = -64; z <= 64; z += 11)
        {
            Assert.False(PebbleGenerator.TryGetPebble(x, z, 0f, out _), "weight 0 must never stamp");
            var sparse = PebbleGenerator.TryGetPebble(x, z, 0.2f, out _);
            var dense = PebbleGenerator.TryGetPebble(x, z, 1f, out _);
            Assert.True(!sparse || dense, $"({x},{z}) qualifies sparsely but not densely");
        }
    }

    [Fact]
    public void StampOnlyLaysPilesOnSurfacesWithWeight()
    {
        var pebble = ContentRegistry.RequireBlock("pebble").Id;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var grass = ContentRegistry.RequireBlock("grass").Id;
        var sand = ContentRegistry.RequireBlock("sand").Id;

        var found = 0;
        // A patch noise cell is ~96 blocks wide, so this spans several
        for (var cx = 0; cx < 12; cx++)
        for (var cz = 0; cz < 12; cz++)
            found += StampStripeChunk(cx, cz, pebble, stone, grass, sand);

        Assert.True(found > 0, "expected at least one pile across the sampled area");
    }

    /// <summary>
    /// Stamps one synthetic chunk whose surface is a diagonal stripe of stone/grass/sand held
    /// above sea level (so no column is wet), then counts the piles and asserts every one of
    /// them sits on a weighted surface.
    /// </summary>
    private static int StampStripeChunk(int chunkX, int chunkZ, byte pebble, byte stone, byte grass, byte sand)
    {
        const int groundLy = 1;
        const int pebbleLy = groundLy + 1;

        var cells = Chunk.Size * Chunk.Size;
        var heights = new int[cells];
        var ground = new byte[cells];
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];

        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var i = (x << Chunk.SizeShift) | z;
            heights[i] = WorldGenerationConfig.SeaLevel;
            ground[i] = ((x + z) % 3) switch
            {
                0 => stone,
                1 => grass,
                _ => sand,
            };
            blocks[Chunk.Idx(x, groundLy, z)] = ground[i];
        }

        var column = new ColumnData(heights, new byte[cells], new float[cells], new float[cells]);
        PebbleGenerator.Stamp(blocks, column, chunkX * Chunk.Size,
            WorldGenerationConfig.SeaLevel - groundLy, chunkZ * Chunk.Size);

        var found = 0;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            if (blocks[Chunk.Idx(x, pebbleLy, z)] != pebble) continue;
            found++;
            var surface = ground[(x << Chunk.SizeShift) | z];
            Assert.True(BlockType.PebbleWeightOf(surface) > 0f,
                $"a pile sat on block {surface}, whose pebbleWeight is zero");
        }

        return found;
    }
}
