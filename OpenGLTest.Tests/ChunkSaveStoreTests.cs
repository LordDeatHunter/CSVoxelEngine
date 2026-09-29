using System;
using System.IO;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

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
}
