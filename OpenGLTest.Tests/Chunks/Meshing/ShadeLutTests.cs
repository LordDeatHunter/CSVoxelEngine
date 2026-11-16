using VoxelEngine.Chunks.Meshing;
using Xunit;

namespace VoxelEngine.Tests.Chunks.Meshing;

/// <summary>The baked AO shade: the darkest AO level dims a face but never blacks it out, and
/// more open corners are never darker.</summary>
public class ShadeLutTests
{
    [Fact]
    public void FullyEnclosedCornerStillReceivesLight()
    {
        // Both sides occluded: the corner every vertex of a 1x1 shaft's floor sits in
        var ao = ChunkFaceTables.VertexAO(true, true, true);
        for (var face = 0; face < 6; face++)
            Assert.True(ChunkFaceTables.ShadeLut[(face << 2) | ao] > 0, $"face {face} renders black at AO {ao}");
    }

    [Fact]
    public void ShadeRisesWithEachOpenAoLevel()
    {
        for (var face = 0; face < 6; face++)
        for (var ao = 1; ao <= 3; ao++)
            Assert.True(ChunkFaceTables.ShadeLut[(face << 2) | ao] > ChunkFaceTables.ShadeLut[(face << 2) | (ao - 1)]);
    }
}
