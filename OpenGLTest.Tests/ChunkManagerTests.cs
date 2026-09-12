using System.Collections.Generic;
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
    public void BorderBreakRemeshesNeighbourSynchronously()
    {
        var manager = new ChunkManager(null!);
        var h = MeshTestWorld.HighAbove;
        var eastPos = h + Vector3.UnitX;
        // The edited chunk and its eastern neighbour each need all six face neighbours
        // loaded for a synchronous build.
        var positions = new HashSet<Vector3> { h, eastPos };
        foreach (var offset in Vector3Directions.CardinalDirections)
        {
            positions.Add(h + offset);
            positions.Add(eastPos + offset);
        }
        foreach (var pos in positions)
        {
            var chunk = new Chunk(null!, pos, generateBlockData: false);
            chunk.IsBlockDataReady = true;
            manager.Chunks[pos] = chunk;
        }

        var stone = ContentRegistry.RequireBlock("stone").Id;
        var center = manager.Chunks[h];
        center.SetBlockRaw(15, 8, 8, stone, 0);

        // Breaking the border cell remeshes the neighbour in the same call: its mesh is
        // current without waiting for the async dirty budget.
        Assert.True(center.RemoveBlockAt(new Vector3(15, 136, 8), manager));
        Assert.True(center.IsMeshed);
        Assert.True(manager.Chunks[eastPos].IsMeshed);
    }

    [Fact]
    public void DirtyScanCapPacesTheDrain()
    {
        var manager = new ChunkManager(null!);
        const int total = 1500;
        for (var i = 0; i < total; i++)
            manager.MarkDirty(new Vector3(5000 + i, 0, 0));

        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(0f, 30f, 0f), yaw: 0f, pitch: 0f);
        manager.Update(camera);

        // Out-of-range entries drop on sight, but only up to the per-frame scan cap;
        // the rest wait for the next frames instead of costing milliseconds at once.
        Assert.Equal(total - WorldGenerationConfig.MaxDirtyScanPerFrame, manager.DirtyCount);
        manager.Update(camera);
        Assert.Equal(0, manager.DirtyCount);
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

    /// <summary>
    /// ChunkRemovalVersion moves only when an unload pass really dropped a chunk, and by the time
    /// it moves the dropped position is already needed again. Both halves are load-bearing for the
    /// generation workers' scan frontier (PERF_TODO C3): the first is why a settled world does not
    /// reset the frontier every frame, and the second is the ordering guard — the bump sits after
    /// the whole unload loop, so it can never be observed while the position is still sitting in
    /// _queuedChunkPositions and therefore still not needed. Both outcomes happen in this one fact,
    /// so it cannot pass vacuously.
    /// </summary>
    [Fact]
    public void UnloadBumpsChunkRemovalVersionOnlyWhenAChunkLeaves()
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;

        var camera = new Camera(null, null);
        camera.TestTeleport(Vector3.Zero, yaw: 0f, pitch: 0f);
        var before = manager.ChunkRemovalVersion;

        // _lastUnloadCamChunk starts null, so this first Update really does run the unload scan -
        // it just finds nothing out of range.
        manager.Update(camera);
        Assert.True(manager.Chunks.ContainsKey(Vector3.Zero));
        Assert.Equal(before, manager.ChunkRemovalVersion);

        camera.TestTeleport(new Vector3(2000f, 30f, 2000f), yaw: 0f, pitch: 0f);
        manager.Update(camera);

        Assert.False(manager.Chunks.ContainsKey(Vector3.Zero));
        Assert.True(manager.ChunkRemovalVersion > before);
        // Fails if the increment is ever moved ahead of the _queuedChunkPositions.TryRemove.
        Assert.True(manager.NeedsChunk(Vector3.Zero));
    }
}
