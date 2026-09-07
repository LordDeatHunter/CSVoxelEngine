using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class ChunkManagerTests
{
    public ChunkManagerTests() => TestContent.EnsureLoaded();

    [Fact]
    public void FirstEmptyMeshBumpsMeshVersion()
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

        var before = manager.MeshVersion;
        manager.ImmediateRemesh(Vector3.Zero);

        Assert.True(center.IsMeshed);
        Assert.False(center.HasMesh);
        Assert.Empty(manager.Renderable);
        Assert.True(manager.MeshVersion > before);

        var after = manager.MeshVersion;
        manager.ImmediateRemesh(Vector3.Zero);
        Assert.Equal(after, manager.MeshVersion);
    }

    [Fact]
    public void UnloadBumpsMeshVersion()
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;

        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(2000f, 30f, 2000f), yaw: 0f, pitch: 0f);
        var before = manager.MeshVersion;
        manager.Update(camera);

        Assert.False(manager.Chunks.ContainsKey(Vector3.Zero));
        Assert.True(manager.MeshVersion > before);
    }
}
