using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>
/// Gravel and sand pockets: blobs of loose rock replace stone only, buried under the surface, so
/// mining into one can collapse it.
/// </summary>
public sealed class GravelPocketTests
{
    public GravelPocketTests() => TestContent.EnsureLoaded();

    [Fact]
    public void PocketsReplaceBuriedStoneOnly()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var gravel = GravelPocketGenerator.GravelBlock;
        var sand = GravelPocketGenerator.SandBlock;

        // A hand-built column: the surface at y=30, solid stone from y=1 to the surface
        var heights = new int[Chunk.Size * Chunk.Size];
        var water = new int[Chunk.Size * Chunk.Size];
        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] = 30;
            water[i] = TerrainGenerator.NoWater;
        }
        var column = new ColumnData(heights, new byte[heights.Length], new float[heights.Length],
            new float[heights.Length], waterLevel: water);

        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 1; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            blocks[Chunk.Idx(x, y, z)] = stone;

        GravelPocketGenerator.Stamp(blocks, column, 0, 16, 0);

        var pockets = 0;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var surfaceHeight = heights[(x << Chunk.SizeShift) | z];
            for (var ly = 1; ly < Chunk.Size; ly++)
            {
                var y = 16 + ly;
                var id = blocks[Chunk.Idx(x, ly, z)];
                var loose = id == gravel || id == sand;
                if (!loose) continue;
                pockets++;
                // Only stone inside a pocket, never within the burial band
                Assert.True(y <= surfaceHeight - WorldGenerationConfig.GravelPocketMinBurial,
                    $"a pocket reached ({x}, {y}, {z}), too close to the surface");
                Assert.True(GravelPocketGenerator.HasPocket(x, y, z, surfaceHeight),
                    $"loose rock at ({x}, {y}, {z}) is outside every pocket");
            }
        }

        Assert.True(pockets > 0, "no pockets in the sampled stone");
    }

    [Fact]
    public void APocketIsAPureFunctionOfTheCell()
    {
        var (x, y, z, height) = (40, 10, -20, 40);
        for (var i = 0; i < 8; i++)
            Assert.Equal(GravelPocketGenerator.HasPocket(x, y, z, height),
                GravelPocketGenerator.HasPocket(x, y, z, height));

        // Above the burial band there is never a pocket
        Assert.False(GravelPocketGenerator.HasPocket(x, height - 1, z, height));
    }
}
