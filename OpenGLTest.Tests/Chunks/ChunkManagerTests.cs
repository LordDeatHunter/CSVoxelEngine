using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.Player;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Chunks;

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
            var neighbor = new Chunk(null!, offset, generateBlockData: false);
            neighbor.IsBlockDataReady = true;
            manager.Chunks[offset] = neighbor;
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
    public void BorderBreakRemeshesNeighborSynchronously()
    {
        var manager = new ChunkManager(null!);
        var h = MeshTestWorld.HighAbove;
        var eastPos = h + Vector3.UnitX;
        // The edited chunk and its eastern neighbor each need all six face neighbors
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

        // Breaking the border cell remeshes the neighbor in the same call: its mesh is
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

        // _lastUnloadFoci starts null, so this first Update really does run the unload scan -
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

    /// <summary>
    /// With several streaming foci a chunk stays loaded while ANY focus keeps it inside the load
    /// radius, and leaves only once it is outside all of them; that pass still bumps
    /// <see cref="ChunkManager.ChunkRemovalVersion"/> once, after the whole loop, so the dropped
    /// position is needed again by the time the bump is visible. A later pass whose foci no longer
    /// include the second player drops that player's chunk too.
    /// </summary>
    [Fact]
    public void UnloadKeepsAChunkInsideAnyFocusRadius()
    {
        var manager = new ChunkManager(null!);
        var first = Vector3.Zero;
        var second = new Vector3(100f, 0f, 0f);
        // Inside the second focus's radius only, and inside neither: the second focus alone must
        // keep the first, and nothing keeps the other.
        var nearSecond = second + new Vector3(1f, 0f, 1f);
        var nearNeither = new Vector3(50f, 0f, 0f);
        Assert.True(Vector3.DistanceSquared(nearSecond, first) > WorldGenerationConfig.ChunkRadiusSq);
        Assert.True(Vector3.DistanceSquared(nearNeither, first) > WorldGenerationConfig.ChunkRadiusSq);
        Assert.True(Vector3.DistanceSquared(nearNeither, second) > WorldGenerationConfig.ChunkRadiusSq);

        foreach (var pos in new[] { first, nearSecond, nearNeither })
        {
            var chunk = new Chunk(null!, pos, generateBlockData: false);
            chunk.IsBlockDataReady = true;
            manager.Chunks[pos] = chunk;
        }

        var focus = new StreamingFocus();
        var center = new Vector3(Chunk.Size / 2f);
        focus.SetEyes([first * Chunk.Size + center, second * Chunk.Size + center]);
        var before = manager.ChunkRemovalVersion;

        manager.Update(focus.Chunks);

        Assert.True(manager.Chunks.ContainsKey(first));
        Assert.True(manager.Chunks.ContainsKey(nearSecond), "a chunk inside the second focus's radius left");
        Assert.False(manager.Chunks.ContainsKey(nearNeither), "a chunk outside every focus's radius stayed");
        Assert.Equal(before + 1, manager.ChunkRemovalVersion);
        Assert.True(manager.NeedsChunk(nearNeither));

        // The second player leaves: its chunk is now outside every remaining focus.
        focus.SetEye(first * Chunk.Size + center);
        manager.Update(focus.Chunks);

        Assert.True(manager.Chunks.ContainsKey(first));
        Assert.False(manager.Chunks.ContainsKey(nearSecond), "the departed focus's chunk stayed loaded");
    }

    /// <summary>
    /// An unload forgets exactly the placements inside the chunk that left, and nothing else
    /// (PERF_TODO C6: the flat set scanned per unload became one bucket per chunk position).
    /// <para>
    /// The ordering below is load-bearing, because the fact would pass vacuously if no unload
    /// ever happened: both chunks are registered and every placement is marked BEFORE the camera
    /// is teleported, and only then does a single Update run. That first Update really does run
    /// the unload scan — _lastUnloadFoci starts null, the same reason
    /// the removal-version fact above carries this comment — and the two guard assertions on
    /// Chunks prove the pass actually dropped the far chunk before any placement is probed.
    /// </para>
    /// <para>
    /// The probes straddle the bucket key from both sides. (640,0,0) and (655,15,15) are the
    /// dropped chunk's inclusive corners, so an off-by-one in the key leaves one of them behind.
    /// (639,0,0) is one block outside it, in a chunk that was never registered and so can never
    /// be unloaded, and (0,0,640) is the axis-swapped image of the dropped chunk: a bucket keyed
    /// on the wrong axis either keeps a corner alive or forgets one of these two.
    /// </para>
    /// </summary>
    [Fact]
    public void UnloadForgetsPlacementsOnlyInsideTheUnloadedChunk()
    {
        var manager = new ChunkManager(null!);
        var farPos = new Vector3(40f, 0f, 0f);
        foreach (var pos in new[] { Vector3.Zero, farPos })
        {
            var chunk = new Chunk(null!, pos, generateBlockData: false);
            chunk.IsBlockDataReady = true;
            manager.Chunks[pos] = chunk;
        }

        // Marked while both chunks are still loaded, so the unload pass below is the only thing
        // that can drop any of them.
        var nearCells = new (int X, int Y, int Z)[] { (0, 0, 0), (7, 3, 9), (15, 15, 15) };
        var farCells = new (int X, int Y, int Z)[] { (640, 0, 0), (655, 15, 15), (647, 8, 4) };
        var neverLoadedCells = new (int X, int Y, int Z)[] { (639, 0, 0), (0, 0, 640) };
        foreach (var (x, y, z) in nearCells) manager.MarkPlayerPlaced(x, y, z);
        foreach (var (x, y, z) in farCells) manager.MarkPlayerPlaced(x, y, z);
        foreach (var (x, y, z) in neverLoadedCells) manager.MarkPlayerPlaced(x, y, z);

        var camera = new Camera(null, null);
        camera.TestTeleport(Vector3.Zero, yaw: 0f, pitch: 0f);
        manager.Update(camera);

        // Guard first: chunk (40,0,0) is 40 chunks out, far past ChunkRadius, while the origin
        // chunk sits under the camera. Without these the placement assertions could pass on a
        // manager that never unloaded anything at all.
        Assert.False(manager.Chunks.ContainsKey(farPos));
        Assert.True(manager.Chunks.ContainsKey(Vector3.Zero));

        foreach (var (x, y, z) in farCells)
            Assert.False(manager.IsPlayerPlaced(x, y, z),
                $"cell ({x},{y},{z}) is inside the unloaded chunk and should have been forgotten");
        foreach (var (x, y, z) in nearCells)
            Assert.True(manager.IsPlayerPlaced(x, y, z),
                $"cell ({x},{y},{z}) is in the chunk that stayed loaded");
        foreach (var (x, y, z) in neverLoadedCells)
            Assert.True(manager.IsPlayerPlaced(x, y, z),
                $"cell ({x},{y},{z}) is in a chunk that was never loaded, so nothing can unload it");
    }

    /// <summary>
    /// Buckets are keyed by the floored chunk position, not a truncated one. The code C6 replaced
    /// derived its bounds with (int)pos.X * Chunk.Size — a truncation that was safe only because
    /// it read Chunks' own integral keys. The accessors need the floor: block (-1,-1,-1) belongs
    /// to chunk (-1,-1,-1), and truncation would file it with (0,0,0) in one shared bucket, where
    /// unloading either chunk would silently take both.
    /// </summary>
    [Fact]
    public void PlacementBucketsAreKeyedByFlooredChunkPosition()
    {
        var manager = new ChunkManager(null!);
        manager.MarkPlayerPlaced(-1, -1, -1);
        manager.MarkPlayerPlaced(0, 0, 0);

        Assert.True(manager.IsPlayerPlaced(-1, -1, -1));
        Assert.True(manager.IsPlayerPlaced(0, 0, 0));
        Assert.Equal(2, manager.PlayerPlacedChunkCount);

        manager.UnmarkPlayerPlaced(0, 0, 0);
        Assert.False(manager.IsPlayerPlaced(0, 0, 0));
        Assert.True(manager.IsPlayerPlaced(-1, -1, -1));
        Assert.Equal(1, manager.PlayerPlacedChunkCount);
    }

    /// <summary>
    /// The anti-leak invariant C6 introduces: a bucket is dropped the moment it empties. Without
    /// it the index keeps one permanent entry per chunk ever edited, which trades the old
    /// per-unload scan for a slow leak instead of removing a cost. The first assertion also pins
    /// that the index is bucketed at all — two cells in one chunk are one bucket, so a flat set
    /// reporting its element count fails here.
    /// </summary>
    [Fact]
    public void UnmarkingTheLastCellDropsItsBucket()
    {
        var manager = new ChunkManager(null!);
        manager.MarkPlayerPlaced(5, 6, 7);
        manager.MarkPlayerPlaced(5, 6, 8);
        Assert.Equal(1, manager.PlayerPlacedChunkCount);

        manager.UnmarkPlayerPlaced(5, 6, 7);
        // Still one cell left, so the bucket stays.
        Assert.Equal(1, manager.PlayerPlacedChunkCount);

        manager.UnmarkPlayerPlaced(5, 6, 8);
        Assert.Equal(0, manager.PlayerPlacedChunkCount);

        // FluidManager and TntManager unmark unconditionally on any block removal, so the miss
        // path must not conjure a bucket to remove a cell from.
        manager.UnmarkPlayerPlaced(100, 100, 100);
        Assert.Equal(0, manager.PlayerPlacedChunkCount);
        Assert.False(manager.IsPlayerPlaced(100, 100, 100));
    }

    /// <summary>
    /// <see cref="ChunkManager.ReleaseAll"/> (world teardown, after the workers joined and the
    /// mesh work drained) leaves no chunk, renderable, dirty entry, pending build, placement or
    /// finished-but-unapplied mesh behind, makes every position needed again, and is idempotent.
    /// The area is walled solid so the center's mesh is empty: the only kind a pool-less
    /// manager can apply.
    /// </summary>
    [Fact]
    public void ReleaseAllEmptiesEveryTableAndFreesEveryMesh()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        MeshTestWorld.FillWalledArea(manager, TerrainGenerator.RockBlock);
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(8f, 8f, 8f), yaw: 0f, pitch: 0f);
        // Generous: this build is often the process's first, and at suite start it runs cold
        // (JIT, worldgen static constructors) against every other test class; the wait returns
        // as soon as the work is done
        var drain = System.TimeSpan.FromSeconds(30);

        manager.MarkDirty(center.Position);
        manager.Update(camera); // schedules the build
        Assert.True(manager.DrainMeshWork(drain), "mesh work did not drain");
        manager.Update(camera); // applies it
        Assert.True(center.IsMeshed);

        // A second build that finishes but is never applied: it sits in the completed queue
        manager.MarkDirty(center.Position);
        manager.Update(camera);
        Assert.True(manager.DrainMeshWork(drain), "mesh work did not drain");

        // Pending state in the tables the release has to empty
        manager.MarkPlayerPlaced(1, 1, 1);
        manager.MarkDirty(center.Position + Vector3.UnitX);
        Assert.True(manager.PlayerPlacedChunkCount > 0);
        Assert.True(manager.DirtyCount > 0);

        var former = new List<Chunk>(manager.Chunks.Values);
        Assert.NotEmpty(former);

        manager.ReleaseAll();

        Assert.Equal(0, manager.Chunks.Count);
        Assert.Empty(manager.Renderable);
        Assert.Empty(manager.TranslucentRenderable);
        Assert.Equal(0, manager.LoadedChunkCount);
        Assert.Equal(0, manager.DirtyCount);
        Assert.Equal(0, manager.PendingMeshWorkCount);
        Assert.Equal(0, manager.GeneratingMeshCount);
        Assert.Equal(0, manager.PlayerPlacedChunkCount);
        foreach (var chunk in former)
        {
            Assert.Null(chunk.OpaqueSlice);
            Assert.Null(chunk.TranslucentSlice);
            Assert.Equal(-1, chunk.RenderIndex);
        }
        Assert.True(manager.NeedsChunk(center.Position));

        // The unapplied build was drained with the release: a following frame applies nothing
        var meshedAfterRelease = center.IsMeshed;
        var versionAfterRelease = manager.MeshVersion;
        manager.Update(camera);
        Assert.Equal(meshedAfterRelease, center.IsMeshed);
        Assert.Equal(versionAfterRelease, manager.MeshVersion);
        Assert.Equal(0, manager.Chunks.Count);

        Assert.Null(Record.Exception(manager.ReleaseAll));
        Assert.Equal(0, manager.Chunks.Count);
    }
}
