using System.Numerics;
using VoxelEngine.WorldGen.Features;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>
/// Climate-scaled grass and flower meadows: the tuft density runs from the dry end of the climate
/// to the wet end, and every flower stands in a clump whose center is open forest.
/// </summary>
public sealed class MeadowTests
{
    public MeadowTests() => TestContent.EnsureLoaded();

    [Fact]
    public void TheTuftDensityRisesWithWetness()
    {
        Assert.Equal(WorldGenerationConfig.GrassDensityDry, FoliageGenerator.GrassDensityAt(0f), 4);
        Assert.Equal(WorldGenerationConfig.GrassDensityWet, FoliageGenerator.GrassDensityAt(1f), 4);
        Assert.True(FoliageGenerator.GrassDensityAt(1f) > FoliageGenerator.GrassDensityAt(0f));
        Assert.True(FoliageGenerator.GrassDensityAt(0.5f) > FoliageGenerator.GrassDensityAt(0.25f));

        // Out-of-range wetness clamps to the ends
        Assert.Equal(FoliageGenerator.GrassDensityAt(0f), FoliageGenerator.GrassDensityAt(-1f));
        Assert.Equal(FoliageGenerator.GrassDensityAt(1f), FoliageGenerator.GrassDensityAt(2f));
    }

    [Fact]
    public void EveryFlowerStandsInAnOpenMeadowClump()
    {
        var flower = FlowerGenerator.FlowerBlock;
        var found = 0;
        for (var cx = -8; cx <= 8; cx++)
        for (var cz = -8; cz <= 8; cz++)
        {
            var chunk = new Chunk(null!, new Vector3(cx, 0, cz), generateBlockData: true);
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                for (var y = 1; y < Chunk.Size; y++)
                {
                    if (chunk.GetLocalBlock(x, y, z) != flower) continue;
                    var worldX = (cx << Chunk.SizeShift) + x;
                    var worldZ = (cz << Chunk.SizeShift) + z;
                    // The clump reaching this column is one whose center is open forest
                    Assert.True(FlowerGenerator.TryGetFlower(worldX, worldZ),
                        $"a flower at ({worldX}, {worldZ}) is not in an open meadow clump");
                    found++;
                }
            }
        }

        Assert.True(found > 0, "no meadow flowers found near the origin");
    }
}
