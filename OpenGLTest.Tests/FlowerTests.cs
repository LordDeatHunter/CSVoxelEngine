using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class FlowerTests
{
    public FlowerTests() => TestContent.EnsureLoaded();

    [Fact]
    public void CrossBakesToTwoUnculledQuads()
    {
        var flower = ContentRegistry.RequireBlock("flower");
        var model = flower.BakedModel!;
        Assert.Equal(2, model.Quads.Length);
        Assert.False(model.IsFullCube);
        foreach (var quad in model.Quads)
        {
            Assert.Equal(-1, quad.CullFace);
            Assert.False(quad.IsFullFace);
            Assert.False(quad.Shade);
        }
    }

    [Fact]
    public void FlowerHasMinecraftLikeFlags()
    {
        var flower = ContentRegistry.RequireBlock("flower");
        Assert.False(flower.Solid);
        Assert.False(flower.OccludesNeighbours);
        Assert.False(flower.CastsAmbientOcclusion);
        Assert.True(flower.Cutout);
        Assert.True(flower.PassesLight);
        Assert.True(flower.NeedsSupport);
        Assert.True(flower.HasItem);
        Assert.Equal(0, flower.MaxHealth);
        Assert.False(BlockType.IsGreedy(flower.Id));
        Assert.False(BlockType.Occludes(flower.Id));
    }

    [Fact]
    public void FlowerEmitsCrossGeometry()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var flower = ContentRegistry.RequireBlock("flower").Id;
        center.SetBlockRaw(8, 8, 8, flower, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(8, mesh.Vertices.Count);
        Assert.Equal(12, mesh.Indices.Count);
        Assert.Empty(mesh.TranslucentVertices);
    }

    [Fact]
    public void FlowersStampOnlyOverGrass()
    {
        var flower = ContentRegistry.RequireBlock("flower").Id;
        var grass = ContentRegistry.RequireBlock("grass").Id;
        var found = 0;

        for (var cx = -4; cx <= 4; cx++)
        for (var cz = -4; cz <= 4; cz++)
        {
            var heights = Chunk.GetColumnHeights(cx, cz);
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
