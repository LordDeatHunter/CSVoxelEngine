using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class MeshingTests
{
    public MeshingTests() => TestContent.EnsureLoaded();

    [Fact]
    public void SingleCubeEmitsSixFaces()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(24, mesh.Vertices.Count);
        Assert.Equal(36, mesh.Indices.Count);
        Assert.Empty(mesh.TranslucentVertices);
    }

    [Fact]
    public void SharedFaceIsCulledBetweenCubes()
    {
        // High above the terrain so sunlight is uniform: the two cubes' shared face is culled
        // and the greedy pass merges the remaining coplanar pairs (+-Y, +-Z) into six quads
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        center.SetBlockRaw(8, 8, 8, stone, 0);
        center.SetBlockRaw(9, 8, 8, stone, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(24, mesh.Vertices.Count);
        Assert.Equal(36, mesh.Indices.Count);

        // The per-quad path keeps the ten faces apart
        var scalar = center.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(scalar);
        Assert.Equal(40, scalar.Vertices.Count);
        Assert.Equal(60, scalar.Indices.Count);
    }

    [Fact]
    public void MissingFaceNeighbourAbandonsTheBuild()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        manager.Chunks.TryRemove(Vector3.UnitX, out _);

        Assert.Null(center.GenerateMeshData(manager));
    }

    [Fact]
    public void AllAirChunkBuildsEmpty()
    {
        var (manager, center) = MeshTestWorld.WalledArea();

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Empty(mesh.Vertices);
        Assert.Empty(mesh.TranslucentVertices);
    }

    [Fact]
    public void GlassQuadsGoToTheTranslucentMesh()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("glass").Id, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Empty(mesh.Vertices);
        Assert.Equal(24, mesh.TranslucentVertices.Count);
        Assert.Equal(36, mesh.TranslucentIndices.Count);
    }

    [Fact]
    public void TouchingGlassCullsTheSharedFaces()
    {
        // Glass casts no AO, so like the stone pair above it merges to six quads
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var glass = ContentRegistry.RequireBlock("glass").Id;
        center.SetBlockRaw(8, 8, 8, glass, 0);
        center.SetBlockRaw(9, 8, 8, glass, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(24, mesh.TranslucentVertices.Count);
        Assert.Equal(36, mesh.TranslucentIndices.Count);

        var scalar = center.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(scalar);
        Assert.Equal(40, scalar.TranslucentVertices.Count);
        Assert.Equal(60, scalar.TranslucentIndices.Count);
    }

    [Fact]
    public void GlassNeverHidesAStoneFace()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);
        center.SetBlockRaw(9, 8, 8, ContentRegistry.RequireBlock("glass").Id, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(24, mesh.Vertices.Count);
        Assert.Equal(20, mesh.TranslucentVertices.Count);
    }

    [Fact]
    public void WaterSurfaceGoesToTheTranslucentMesh()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("water").Id, FluidState.Source);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Empty(mesh.Vertices);
        Assert.NotEmpty(mesh.TranslucentVertices);
    }

    [Fact]
    public void EnclosedChunkBuildsEmpty()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        MeshTestWorld.FillWalledArea(manager, ContentRegistry.RequireBlock("stone").Id);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Empty(mesh.Vertices);
        Assert.Empty(mesh.TranslucentVertices);
    }

    // One air cell in the neighbour plane that touches the centre exposes exactly one face,
    // which also pins the orientation of each boundary plane the fast path inspects.
    [Theory]
    [InlineData(1, 0, 0, 0, 8, 8)]
    [InlineData(-1, 0, 0, 15, 8, 8)]
    [InlineData(0, 1, 0, 8, 0, 8)]
    [InlineData(0, -1, 0, 8, 15, 8)]
    [InlineData(0, 0, 1, 8, 8, 0)]
    [InlineData(0, 0, -1, 8, 8, 15)]
    public void HoleInNeighbourPlaneEmitsOneFace(int ox, int oy, int oz, int hx, int hy, int hz)
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        MeshTestWorld.FillWalledArea(manager, ContentRegistry.RequireBlock("stone").Id);
        manager.Chunks[new Vector3(ox, oy, oz)].SetBlockRaw(hx, hy, hz, 0, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(4, mesh.Vertices.Count);
        Assert.Equal(6, mesh.Indices.Count);
    }

    [Fact]
    public void AirPocketInsideSolidChunkStillMeshes()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        MeshTestWorld.FillWalledArea(manager, ContentRegistry.RequireBlock("stone").Id);
        center.SetBlockRaw(8, 8, 8, 0, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(24, mesh.Vertices.Count);
    }

    [Fact]
    public void ReturnedBuffersAreRentedAgain()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);

        var first = center.GenerateMeshData(manager);
        Assert.NotNull(first);
        Assert.Equal(0, manager.MeshBuffers.PooledVertexLists);

        manager.MeshBuffers.Return(first);
        Assert.Equal(1, manager.MeshBuffers.PooledVertexLists);

        var second = center.GenerateMeshData(manager);
        Assert.NotNull(second);
        Assert.Same(first.Vertices, second.Vertices);
        Assert.Same(first.Indices, second.Indices);
        Assert.Equal(24, second.Vertices.Count);
        Assert.Equal(0, manager.MeshBuffers.PooledVertexLists);
    }

    [Fact]
    public void OversizedBuffersAreNotRetained()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        mesh.Vertices.Capacity = MeshBufferPool.VertexCapacity * 8;

        manager.MeshBuffers.Return(mesh);
        Assert.Equal(0, manager.MeshBuffers.PooledVertexLists);
    }

    [Fact]
    public void VertexPackingRoundTrips()
    {
        Assert.Equal(32u, Vertex.Size);
        // Tile-local UVs are floats: a merged quad spans whole tiles, a font glyph a sub-texel
        // rectangle, and both have to come back exactly
        var v = new Vertex(new Vector3(1f, 2f, 3f), 10, 20, 30, 255,
            new Vector2(13.5f, 0.25f), 300, 7f / 15f, 3f / 15f, new Vector3(0f, 0f, 1f), 1f);
        Assert.Equal(13.5f, v.UV.X);
        Assert.Equal(0.25f, v.UV.Y);
        Assert.Equal(300, v.Layer);
        Assert.Equal(0, v.Reserved);
        // The single-slice overload lands on layer 0 with the UV untouched
        var sprite = new Vertex(Vector3.Zero, 255, 255, 255, 255, new Vector2(0.125f, 0.875f));
        Assert.Equal(0, sprite.Layer);
        Assert.Equal(0.125f, sprite.UV.X);
        Assert.Equal(7f / 15f, v.Sun / 255f, 3);
        Assert.Equal(3f / 15f, v.Block / 255f, 3);
        Assert.Equal(1, v.Normal);
        Assert.Equal(255, v.Sway);
        foreach (var (normal, index) in new[]
                 {
                     (new Vector3(0, 0, -1), 0), (new Vector3(0, 0, 1), 1),
                     (new Vector3(-1, 0, 0), 2), (new Vector3(1, 0, 0), 3),
                     (new Vector3(0, 1, 0), 4), (new Vector3(0, -1, 0), 5),
                 })
            Assert.Equal(index, new Vertex(Vector3.Zero, 255, 255, 255, 255,
                Vector2.Zero, 1f, 0f, normal, 0f).Normal);

        // Bit 3 rides alongside the face index rather than replacing it
        var submerged = new Vertex(Vector3.Zero, 255, 255, 255, 255,
            Vector2.Zero, 1f, 0f, Vector3.UnitY, 0f, submerged: true);
        Assert.Equal(4 | 8, submerged.Normal);
        Assert.Equal(4, submerged.Normal & 7);
    }

    [Fact]
    public void SubmergedFacesAreFlagged()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);
        center.SetBlockRaw(8, 9, 8, ContentRegistry.RequireBlock("water").Id, FluidState.Source);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);

        var topVertices = 0;
        foreach (var vertex in mesh.Vertices)
        {
            var face = vertex.Normal & 7;
            var submerged = (vertex.Normal & 8) != 0;
            if (face == 4)
            {
                // The stone's top face is the only one looking into the water above it
                Assert.Equal(9f, vertex.Position.Y);
                Assert.True(submerged);
                topVertices++;
            }
            else
            {
                Assert.False(submerged);
            }
        }

        Assert.Equal(4, topVertices);
    }

    [Fact]
    public void WaterChunkReportsHasWater()
    {
        var (waterManager, waterChunk) = MeshTestWorld.WalledArea();
        waterChunk.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("water").Id, FluidState.Source);
        var waterMesh = waterChunk.GenerateMeshData(waterManager);
        Assert.NotNull(waterMesh);
        Assert.True(waterMesh.HasWater);
        Assert.NotEmpty(waterMesh.TranslucentVertices);

        // Glass lands in the same translucent lists, so a translucent mesh is not proof of water
        var (glassManager, glassChunk) = MeshTestWorld.WalledArea();
        glassChunk.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("glass").Id, 0);
        var glassMesh = glassChunk.GenerateMeshData(glassManager);
        Assert.NotNull(glassMesh);
        Assert.False(glassMesh.HasWater);
        Assert.NotEmpty(glassMesh.TranslucentVertices);

        var (stoneManager, stoneChunk) = MeshTestWorld.WalledArea();
        stoneChunk.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("stone").Id, 0);
        var stoneMesh = stoneChunk.GenerateMeshData(stoneManager);
        Assert.NotNull(stoneMesh);
        Assert.False(stoneMesh.HasWater);
    }
}
