using System;
using System.IO;
using System.Linq;
using VoxelEngine.Saves;
using Xunit;

namespace VoxelEngine.Tests.Saves;

/// <summary>
/// <see cref="ChunkSaveStore"/>: an enqueued chunk is readable at once from memory and after the
/// writer lands it from disk, a newer snapshot for the same key wins, a write that cannot land keeps
/// the chunk readable in memory, a crash's leftover temp files are discarded at open, and the
/// whole-file side cars round-trip. Every test runs against its own temp world folder.
/// </summary>
public sealed class ChunkSaveStoreTests : IDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly string _world = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ChunkSaveStoreTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        if (Directory.Exists(_world))
            Directory.Delete(_world, recursive: true);
    }

    private string ChunksDir => Path.Combine(_world, WorldStore.ChunksDirectoryName);

    // Real block ids, since the store maps ids to names through the loaded content
    private static ChunkSnapshot Snapshot(string fillBlock, int seed)
    {
        var fill = ContentRegistry.RequireBlock(fillBlock).Id;
        var random = new Random(seed);
        var blocks = new byte[Chunk.BlockCount];
        var state = new byte[Chunk.BlockCount];
        for (var i = 0; i < blocks.Length; i++)
        {
            blocks[i] = random.Next(3) == 0 ? fill : (byte)0;
            state[i] = (byte)random.Next(4);
        }
        return new ChunkSnapshot(blocks, state, [3, 17, 4095]);
    }

    private static void AssertSameData(ChunkSnapshot expected, ChunkSnapshot actual)
    {
        Assert.Equal(expected.Blocks, actual.Blocks);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.PlayerPlaced, actual.PlayerPlaced);
    }

    private static void ShutdownOrFail(ChunkSaveStore store) =>
        Assert.True(store.Shutdown(ShutdownTimeout), "the writer never finished");

    [Fact]
    public void EnqueuedSnapshotIsReadableBeforeAndAfterTheWriterFlushes()
    {
        var key = ChunkKey.Pack(-3, 5, 12);
        var snapshot = Snapshot("stone", seed: 1);

        var store = new ChunkSaveStore(_world);
        try
        {
            store.Enqueue(key, snapshot);
            Assert.True(store.TryLoad(key, out var pending));
            AssertSameData(snapshot, pending);
        }
        finally
        {
            ShutdownOrFail(store);
        }

        Assert.Equal(0, store.PendingCount);
        Assert.True(store.IsIndexed(key));

        var reopened = new ChunkSaveStore(_world);
        try
        {
            Assert.True(reopened.IsIndexed(key));
            Assert.True(reopened.TryLoad(key, out var fromDisk));
            AssertSameData(snapshot, fromDisk);
        }
        finally
        {
            ShutdownOrFail(reopened);
        }
    }

    [Fact]
    public void UnknownKeyIsAMissAndTouchesNoFile()
    {
        var store = new ChunkSaveStore(_world);
        try
        {
            Assert.False(store.TryLoad(ChunkKey.Pack(1, 2, 3), out _));
        }
        finally
        {
            ShutdownOrFail(store);
        }

        Assert.Empty(Directory.GetFiles(ChunksDir));
    }

    [Fact]
    public void NewerSnapshotForTheSameKeyWins()
    {
        var key = ChunkKey.Pack(0, 0, 0);
        var older = Snapshot("stone", seed: 2);
        var newer = Snapshot("dirt", seed: 3);

        var store = new ChunkSaveStore(_world);
        try
        {
            store.Enqueue(key, older);
            store.Enqueue(key, newer);
            Assert.True(store.TryLoad(key, out var pending));
            AssertSameData(newer, pending);
        }
        finally
        {
            ShutdownOrFail(store);
        }

        var reopened = new ChunkSaveStore(_world);
        try
        {
            Assert.True(reopened.TryLoad(key, out var fromDisk));
            AssertSameData(newer, fromDisk);
            Assert.NotEqual(older.Blocks, fromDisk.Blocks);
        }
        finally
        {
            ShutdownOrFail(reopened);
        }
    }

    [Fact]
    public void FailedMoveKeepsThePendingSnapshot()
    {
        var key = ChunkKey.Pack(7, -1, 7);
        var snapshot = Snapshot("stone", seed: 4);

        var store = new ChunkSaveStore(_world)
        {
            MoveFile = (_, _) => throw new IOException("locked")
        };
        try
        {
            store.Enqueue(key, snapshot);
        }
        finally
        {
            ShutdownOrFail(store);
        }

        Assert.Equal(1, store.PendingCount);
        Assert.True(store.TryLoad(key, out var pending));
        AssertSameData(snapshot, pending);
        Assert.False(store.IsIndexed(key));
        Assert.False(File.Exists(Path.Combine(ChunksDir, WorldStore.ChunkFileName(7, -1, 7))));

        var reopened = new ChunkSaveStore(_world);
        try
        {
            Assert.False(reopened.TryLoad(key, out _));
            Assert.Empty(Directory.GetFiles(ChunksDir));
        }
        finally
        {
            ShutdownOrFail(reopened);
        }
    }

    [Fact]
    public void LeftoverTmpFilesAreRemovedAtOpen()
    {
        Directory.CreateDirectory(ChunksDir);
        var chunkTmp = Path.Combine(ChunksDir, WorldStore.ChunkFileName(1, 2, 3) + ".tmp");
        var sideCarTmp = Path.Combine(_world, "lod-edits.bin.tmp");
        File.WriteAllBytes(chunkTmp, [1, 2, 3]);
        File.WriteAllBytes(sideCarTmp, [4, 5, 6]);

        var store = new ChunkSaveStore(_world);
        try
        {
            Assert.False(File.Exists(chunkTmp));
            Assert.False(File.Exists(sideCarTmp));
            Assert.False(store.TryLoad(ChunkKey.Pack(1, 2, 3), out _));
        }
        finally
        {
            ShutdownOrFail(store);
        }
    }

    [Fact]
    public void FileJobsRoundTrip()
    {
        var bytes = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7)).ToArray();

        var store = new ChunkSaveStore(_world);
        try
        {
            store.EnqueueFile("lod-edits.bin", bytes);
            Assert.Equal(bytes, store.ReadFile("lod-edits.bin"));
        }
        finally
        {
            ShutdownOrFail(store);
        }

        Assert.Equal(bytes, store.ReadFile("lod-edits.bin"));
        Assert.Null(store.ReadFile("missing.bin"));

        var reopened = new ChunkSaveStore(_world);
        try
        {
            Assert.Equal(bytes, reopened.ReadFile("lod-edits.bin"));
            Assert.Throws<ArgumentException>(() => reopened.ReadFile(Path.Combine("..", "escape.bin")));
        }
        finally
        {
            ShutdownOrFail(reopened);
        }
    }

    [Fact]
    public void AFailedWriteIsRetriedAtShutdownWithoutANewEdit()
    {
        var key = ChunkKey.Pack(2, 0, 2);
        var snapshot = Snapshot("stone", seed: 9);
        var failures = 0;
        var store = new ChunkSaveStore(_world);
        // Every attempt of the first write fails (a scanner holding the file); the retry at
        // shutdown finds the file free again.
        store.MoveFile = (tmp, final) =>
        {
            if (failures <= WorldGenerationConfig.SaveMoveRetries)
            {
                failures++;
                throw new IOException("locked");
            }
            File.Move(tmp, final, overwrite: true);
        };

        store.Enqueue(key, snapshot);
        // Wait for the first write to give up
        var deadline = DateTime.UtcNow + ShutdownTimeout;
        while (store.FailedCount == 0 && DateTime.UtcNow < deadline)
            System.Threading.Thread.Sleep(10);
        Assert.Equal(1, store.FailedCount);

        ShutdownOrFail(store);
        Assert.Equal(0, store.FailedCount);
        Assert.Equal(0, store.PendingCount);
        Assert.True(store.IsIndexed(key));

        var reopened = new ChunkSaveStore(_world);
        try
        {
            Assert.True(reopened.TryLoad(key, out var loaded));
            AssertSameData(snapshot, loaded);
        }
        finally
        {
            ShutdownOrFail(reopened);
        }
    }

    [Fact]
    public void AFolderWithALiveWriterCannotBeOpenedAgain()
    {
        var key = ChunkKey.Pack(1, 0, 1);
        var release = new System.Threading.ManualResetEventSlim(false);
        var store = new ChunkSaveStore(_world);
        store.MoveFile = (tmp, final) =>
        {
            release.Wait();
            File.Move(tmp, final, overwrite: true);
        };
        store.Enqueue(key, Snapshot("dirt", seed: 1));

        // The writer is stuck inside the move: the shutdown overruns and the folder stays locked
        Assert.False(store.Shutdown(TimeSpan.FromMilliseconds(100)));
        Assert.Throws<IOException>(() => new ChunkSaveStore(_world));

        release.Set();
        ShutdownOrFail(store);
        var reopened = new ChunkSaveStore(_world);
        try
        {
            Assert.True(reopened.TryLoad(key, out _));
        }
        finally
        {
            ShutdownOrFail(reopened);
        }
    }

    [Fact]
    public void AnUnreadableChunkFileIsSetAsideNotOverwritten()
    {
        var key = ChunkKey.Pack(5, 1, 5);
        var path = Path.Combine(ChunksDir, WorldStore.ChunkFileName(5, 1, 5));
        Directory.CreateDirectory(ChunksDir);
        var garbage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        File.WriteAllBytes(path, garbage);

        var store = new ChunkSaveStore(_world);
        try
        {
            Assert.True(store.IsIndexed(key));
            Assert.False(store.TryLoad(key, out _));

            var fresh = Snapshot("stone", seed: 3);
            store.Enqueue(key, fresh);
            ShutdownOrFail(store);

            Assert.Equal(garbage, File.ReadAllBytes(path + ".bad"));
            Assert.True(store.TryLoad(key, out var written));
            AssertSameData(fresh, written);
        }
        finally
        {
            store.Shutdown(ShutdownTimeout);
        }
    }
}
