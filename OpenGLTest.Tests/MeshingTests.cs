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
}
