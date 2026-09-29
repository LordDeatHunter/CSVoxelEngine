using System;
using System.IO;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <see cref="ChunkManager"/> with a <see cref="ChunkSaveStore"/> attached: an edited chunk that
/// unloads comes back with its edits (from the pending snapshot at once, from disk once the
/// writer landed it), an untouched chunk is never written, player-placed cells travel with their
/// chunk, and <see cref="ChunkManager.FlushModified"/> saves loaded edits without unloading.
/// Every test runs against its own temp world folder.
/// </summary>
public sealed class ChunkPersistenceTests : IDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    // Every chunk the generation path builds up here is all air, so an edit is the only
    // thing that can make two chunks differ
    private static readonly Vector3 Pos = MeshTestWorld.HighAbove;
    private static readonly long Key = ChunkKey.Pack(Pos);

    private readonly string _world = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ChunkPersistenceTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        if (Directory.Exists(_world))
            Directory.Delete(_world, recursive: true);
    }

    private static byte Block(string name) => ContentRegistry.RequireBlock(name).Id;

    private static void ShutdownOrFail(ChunkSaveStore store) =>
        Assert.True(store.Shutdown(ShutdownTimeout), "the writer never finished");

    private static Camera CameraAt(Vector3 position)
    {
        var camera = new Camera(null, null);
        camera.TestTeleport(position, yaw: 0f, pitch: 0f);
        return camera;
    }

    // Far enough that every chunk near the origin leaves the load sphere
    private static void UnloadEverything(ChunkManager manager) =>
        manager.Update(CameraAt(new Vector3(2000f, 30f, 2000f)));

    private static void Edit(Chunk chunk)
    {
        chunk.SetBlockRaw(3, 4, 5, Block("stone"), 0);
        chunk.SetBlockRaw(6, 2, 6, Block("water"), FluidState.Flowing(3));
    }

    private static void AssertHasEdit(Chunk chunk)
    {
        Assert.Equal(Block("stone"), chunk.GetLocalBlock(3, 4, 5));
        Assert.Equal(Block("water"), chunk.GetLocalBlock(6, 2, 6));
        Assert.Equal(FluidState.Flowing(3), chunk.GetLocalState(6, 2, 6));
        Assert.Equal(0, chunk.GetLocalBlock(0, 0, 0));
    }

    [Fact]
    public void EditedChunkComesBackAfterUnloadAndReload()
    {
        var store = new ChunkSaveStore(_world);
        try
        {
            var manager = new ChunkManager(null!, saveStore: store);
            manager.QueueChunk(Pos);
            var original = manager.Chunks[Pos];
            Assert.False(original.IsRestored);
            Edit(original);

            UnloadEverything(manager);
            Assert.False(manager.Chunks.ContainsKey(Pos));

            // Straight back, before the writer is waited on: the pending snapshot answers
            manager.QueueChunk(Pos);
            var restored = manager.Chunks[Pos];
            Assert.NotSame(original, restored);
            Assert.True(restored.IsRestored);
            Assert.True(restored.IsBlockDataReady);
            AssertHasEdit(restored);
        }
        finally
        {
            ShutdownOrFail(store);
        }

        // A fresh session over the same folder reads it back from disk
        var reopened = new ChunkSaveStore(_world);
        try
        {
            var manager = new ChunkManager(null!, saveStore: reopened);
            manager.QueueChunk(Pos);
            var fromDisk = manager.Chunks[Pos];
            Assert.True(fromDisk.IsRestored);
            AssertHasEdit(fromDisk);
        }
        finally
        {
            ShutdownOrFail(reopened);
        }
    }

    [Fact]
    public void UnmodifiedChunkIsNeverWritten()
    {
        var store = new ChunkSaveStore(_world);
        try
        {
            var manager = new ChunkManager(null!, saveStore: store);
            manager.QueueChunk(Pos);
            UnloadEverything(manager);
            Assert.False(manager.Chunks.ContainsKey(Pos));
        }
        finally
        {
            ShutdownOrFail(store);
        }

        Assert.Empty(Directory.GetFiles(store.ChunksDirectory, "*.bin"));
        Assert.False(store.TryLoad(Key, out _));

        // Re-queueing therefore generates again rather than restoring
        var reopened = new ChunkSaveStore(_world);
        try
        {
            var manager = new ChunkManager(null!, saveStore: reopened);
            manager.QueueChunk(Pos);
            Assert.False(manager.Chunks[Pos].IsRestored);
        }
        finally
        {
            ShutdownOrFail(reopened);
        }
    }

    [Fact]
    public void PlayerPlacedCellsAreRestoredWithTheChunk()
    {
        var store = new ChunkSaveStore(_world);
        try
        {
            var manager = new ChunkManager(null!, saveStore: store);
            manager.QueueChunk(Pos);
            foreach (var offset in Vector3Directions.CardinalDirections)
                manager.QueueChunk(Pos + offset);

            // Solid stone all round keeps every remesh empty, which the headless manager can
            // apply without a geometry pool
            MeshTestWorld.FillWalledArea(manager, Block("stone"));
            var chunk = manager.Chunks[Pos];
            chunk.SetBlockRaw(8, 8, 8, 0, 0);

            var placed = chunk.WorldMin + new Vector3(8, 8, 8);
            var untouched = chunk.WorldMin + new Vector3(8, 9, 8);
            Assert.True(chunk.PlaceBlockAt(placed, Block("dirt"), manager));
            Assert.True(manager.IsPlayerPlaced((int)placed.X, (int)placed.Y, (int)placed.Z));

            UnloadEverything(manager);
            Assert.False(manager.IsPlayerPlaced((int)placed.X, (int)placed.Y, (int)placed.Z));

            manager.QueueChunk(Pos);
            var restored = manager.Chunks[Pos];
            Assert.True(restored.IsRestored);
            Assert.Equal(Block("dirt"), restored.GetLocalBlock(8, 8, 8));

            // The adoption drain runs on the main thread at the top of Update
            manager.Update(CameraAt(restored.WorldMin));
            Assert.True(manager.IsPlayerPlaced((int)placed.X, (int)placed.Y, (int)placed.Z));
            Assert.False(manager.IsPlayerPlaced((int)untouched.X, (int)untouched.Y, (int)untouched.Z));
            Assert.Null(restored.RestoredPlayerPlaced);
        }
        finally
        {
            ShutdownOrFail(store);
        }
    }

    [Fact]
    public void UnadoptedRestoredPlacementsSurviveAFlushBeforeAdoption()
    {
        var placedIndex = (ushort)Chunk.Idx(2, 3, 4);
        var blocks = new byte[Chunk.BlockCount];
        blocks[placedIndex] = Block("dirt");

        var store = new ChunkSaveStore(_world);
        try
        {
            store.Enqueue(Key, new ChunkSnapshot(blocks, null, [placedIndex]));

            var manager = new ChunkManager(null!, saveStore: store);
            manager.QueueChunk(Pos);
            var chunk = manager.Chunks[Pos];
            Assert.True(chunk.IsRestored);
            Assert.NotNull(chunk.RestoredPlayerPlaced);

            // No Update, so the placement is still only on the chunk, not in the index
            chunk.SetBlockRaw(9, 9, 9, Block("stone"), 0);
            Assert.True(manager.FlushModified(int.MaxValue) > 0);
        }
        finally
        {
            ShutdownOrFail(store);
        }

        var reopened = new ChunkSaveStore(_world);
        try
        {
            Assert.True(reopened.TryLoad(Key, out var snapshot));
            Assert.Equal(Block("stone"), snapshot.Blocks[Chunk.Idx(9, 9, 9)]);
            Assert.NotNull(snapshot.PlayerPlaced);
            Assert.Contains(placedIndex, snapshot.PlayerPlaced!);
        }
        finally
        {
            ShutdownOrFail(reopened);
        }
    }

    [Fact]
    public void FlushModifiedWritesLoadedEditsWithoutUnloading()
    {
        var other = Pos + new Vector3(3, 0, 0);
        var store = new ChunkSaveStore(_world);
        try
        {
            var manager = new ChunkManager(null!, saveStore: store);
            manager.QueueChunk(Pos);
            manager.QueueChunk(other);
            var chunk = manager.Chunks[Pos];

            Edit(chunk);
            Assert.True(manager.FlushModified(int.MaxValue) > 0);
            Assert.False(chunk.ModifiedSinceSave);
            Assert.Equal(0, manager.FlushModified(int.MaxValue));
            Assert.True(manager.Chunks.ContainsKey(Pos));

            // The cap paces a burst: one chunk per call until none is left
            chunk.SetBlockRaw(1, 1, 1, Block("stone"), 0);
            manager.Chunks[other].SetBlockRaw(1, 1, 1, Block("stone"), 0);
            Assert.Equal(1, manager.FlushModified(1));
            Assert.Equal(1, manager.FlushModified(1));
            Assert.Equal(0, manager.FlushModified(1));
        }
        finally
        {
            ShutdownOrFail(store);
        }

        var reopened = new ChunkSaveStore(_world);
        try
        {
            var manager = new ChunkManager(null!, saveStore: reopened);
            manager.QueueChunk(Pos);
            AssertHasEdit(manager.Chunks[Pos]);
            Assert.Equal(Block("stone"), manager.Chunks[Pos].GetLocalBlock(1, 1, 1));
        }
        finally
        {
            ShutdownOrFail(reopened);
        }
    }

    [Fact]
    public void PendingSnapshotWinsOverDiskUntilTheWriterCatchesUp()
    {
        var store = new ChunkSaveStore(_world)
        {
            // The writer can never land anything
            MoveFile = (_, _) => throw new IOException("locked"),
        };
        try
        {
            var manager = new ChunkManager(null!, saveStore: store);
            manager.QueueChunk(Pos);
            Edit(manager.Chunks[Pos]);
            UnloadEverything(manager);

            var chunk = manager.CreateChunk(Pos, Key);
            Assert.True(chunk.IsRestored);
            AssertHasEdit(chunk);
        }
        finally
        {
            ShutdownOrFail(store);
        }

        Assert.DoesNotContain(Directory.GetFiles(store.ChunksDirectory), f => f.EndsWith(".bin"));
        var reopened = new ChunkSaveStore(_world);
        try
        {
            Assert.False(reopened.TryLoad(Key, out _));
        }
        finally
        {
            ShutdownOrFail(reopened);
        }
    }
}
