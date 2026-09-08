using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class MeshingTests
{
    public MeshingTests() => TestContent.EnsureLoaded();

    private static (ChunkManager Manager, Chunk Center) WalledArea()
    {
        var manager = new ChunkManager(null!);
        var center = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        center.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = center;
        foreach (var offset in Vector3Directions.CardinalDirections)
        {
            var neighbour = new Chunk(null!, offset, generateBlockData: false);
            neighbour.IsBlockDataReady = true;
            manager.Chunks[offset] = neighbour;
        }
        return (manager, center);
    }

    private static void FillAll(Chunk chunk, byte id)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, y, z, id, 0);
    }

    private static void FillWalledArea(ChunkManager manager, byte id)
    {
        foreach (var chunk in manager.Chunks.Values) FillAll(chunk, id);
    }

    [Fact]
    public void SingleCubeEmitsSixFaces()
    {
        var (manager, center) = WalledArea();
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
        var (manager, center) = WalledArea();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        center.SetBlockRaw(8, 8, 8, stone, 0);
        center.SetBlockRaw(9, 8, 8, stone, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(40, mesh.Vertices.Count);
        Assert.Equal(60, mesh.Indices.Count);
    }

    [Fact]
    public void MissingFaceNeighbourAbandonsTheBuild()
    {
        var (manager, center) = WalledArea();
        manager.Chunks.TryRemove(Vector3.UnitX, out _);

        Assert.Null(center.GenerateMeshData(manager));
    }

    [Fact]
    public void AllAirChunkBuildsEmpty()
    {
        var (manager, center) = WalledArea();

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Empty(mesh.Vertices);
        Assert.Empty(mesh.TranslucentVertices);
    }

    [Fact]
    public void GlassQuadsGoToTheTranslucentMesh()
    {
        var (manager, center) = WalledArea();
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
        var (manager, center) = WalledArea();
        var glass = ContentRegistry.RequireBlock("glass").Id;
        center.SetBlockRaw(8, 8, 8, glass, 0);
        center.SetBlockRaw(9, 8, 8, glass, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(40, mesh.TranslucentVertices.Count);
        Assert.Equal(60, mesh.TranslucentIndices.Count);
    }

    [Fact]
    public void GlassNeverHidesAStoneFace()
    {
        var (manager, center) = WalledArea();
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
        var (manager, center) = WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("water").Id, FluidState.Source);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Empty(mesh.Vertices);
        Assert.NotEmpty(mesh.TranslucentVertices);
    }

    [Fact]
    public void EnclosedChunkBuildsEmpty()
    {
        var (manager, center) = WalledArea();
        FillWalledArea(manager, ContentRegistry.RequireBlock("stone").Id);

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
        var (manager, center) = WalledArea();
        FillWalledArea(manager, ContentRegistry.RequireBlock("stone").Id);
        manager.Chunks[new Vector3(ox, oy, oz)].SetBlockRaw(hx, hy, hz, 0, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(4, mesh.Vertices.Count);
        Assert.Equal(6, mesh.Indices.Count);
    }

    [Fact]
    public void AirPocketInsideSolidChunkStillMeshes()
    {
        var (manager, center) = WalledArea();
        FillWalledArea(manager, ContentRegistry.RequireBlock("stone").Id);
        center.SetBlockRaw(8, 8, 8, 0, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(24, mesh.Vertices.Count);
    }

    [Fact]
    public void ReturnedBuffersAreRentedAgain()
    {
        var (manager, center) = WalledArea();
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
        var (manager, center) = WalledArea();
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
        Assert.Equal(24u, Vertex.Size);
        var v = new Vertex(new Vector3(1f, 2f, 3f), 10, 20, 30, 255,
            new Vector2(0.25f, 0.75f), 7f / 15f, 3f / 15f, new Vector3(0f, 0f, 1f), 1f);
        Assert.Equal(0.25f, v.UV.X, 3);
        Assert.Equal(0.75f, v.UV.Y, 3);
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
    }
}
