using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class FlowerTests
{
    public FlowerTests() => TestContent.EnsureLoaded();

    [Fact]
    public void FlowersStampOnlyOverGrass()
    {
        var flower = ContentRegistry.RequireBlock("flower").Id;
        var grass = ContentRegistry.RequireBlock("grass").Id;
        var found = 0;

        for (var cx = -4; cx <= 4; cx++)
        for (var cz = -4; cz <= 4; cz++)
        {
            var heights = ColumnCache.GetHeights(cx, cz);
            var max = 0;
            foreach (var h in heights)
                if (h > max) max = h;
            var chunk = new Chunk(null!, new Vector3(cx, max >> Chunk.SizeShift, cz),
                generateBlockData: true);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                if (chunk.GetLocalBlock(x, y, z) != flower) continue;
                found++;
                var wx = cx * Chunk.Size + x;
                var wz = cz * Chunk.Size + z;
                var wy = (int)chunk.Position.Y * Chunk.Size + y;
                Assert.True(y > 0);
                Assert.Equal(grass, chunk.GetLocalBlock(x, y - 1, z));
                Assert.False(TerrainGenerator.IsSubmerged(wy - 1));
                Assert.False(TerrainGenerator.IsBeachHeight(wy - 1));
                Assert.True(FlowerGenerator.TryGetFlower(wx, wz));
            }
        }

        Assert.True(found > 0, "expected at least one flower in the 9x9 chunk area");
    }
}
