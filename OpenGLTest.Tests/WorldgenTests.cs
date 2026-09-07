using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class WorldgenTests
{
    public WorldgenTests() => TestContent.EnsureLoaded();

    private static int SurfaceChunkY()
    {
        var heights = Chunk.GetColumnHeights(0, 0);
        var max = 0;
        foreach (var h in heights)
            if (h > max) max = h;
        return max >> Chunk.SizeShift;
    }

    [Fact]
    public void SameChunkGeneratesIdenticalBlocks()
    {
        var pos = new Vector3(0, SurfaceChunkY(), 0);
        var a = new Chunk(null!, pos, generateBlockData: true);
        var b = new Chunk(null!, pos, generateBlockData: true);

        Assert.Equal(a.SurfaceMinY, b.SurfaceMinY);
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            Assert.Equal(a.GetLocalBlock(x, y, z), b.GetLocalBlock(x, y, z));
    }

    [Fact]
    public void OnlyTreeBlocksStandAboveTheHeightmap()
    {
        var cy = SurfaceChunkY();
        var pos = new Vector3(0, cy, 0);
        var chunk = new Chunk(null!, pos, generateBlockData: true);
        var heights = Chunk.GetColumnHeights(0, 0);

        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var h = heights[(x << Chunk.SizeShift) | z];
            for (var ly = 0; ly < Chunk.Size; ly++)
            {
                if (cy * Chunk.Size + ly <= h) continue;
                var id = chunk.GetLocalBlock(x, ly, z);
                Assert.True(!BlockType.IsSolid(id) || TreeGenerator.IsTreeBlock(id),
                    $"solid non-tree {id} above height {h} at ({x},{ly},{z})");
            }
        }
    }
}
