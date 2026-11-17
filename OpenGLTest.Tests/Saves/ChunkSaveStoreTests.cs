using System;
using System.IO;
using System.Linq;
using System.Threading;
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
        var blocks = new ushort[Chunk.BlockCount];
        var state = new byte[Chunk.BlockCount];
        for (var i = 0; i < blocks.Length; i++)
        {
            blocks[i] = random.Next(3) == 0 ? fill : BlockType.Air;
            state[i] = (byte)random.Next(4);
        }
        return new ChunkSnapshot(blocks, state, playerPlaced: [3, 17, 4095]);
    }

    private static void AssertSameData(ChunkSnapshot expected, ChunkSnapshot actual)
    {
        Assert.Equal(expected.Storage.ToDense(), actual.Storage.ToDense());
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
            Assert.NotEqual(older.Storage.ToDense(), fromDisk.Storage.ToDense());
        }
        finally
        {
            ShutdownOrFail(reopened);
        }
    }

    [Fact]
    public void RepeatedSavesOfAWaitingChunkKeepOneJobAndWriteOnlyTheNewest()
    {
        var blockerKey = ChunkKey.Pack(0, 0, 0);
        var key = ChunkKey.Pack(1, 0, 0);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var targetWrites = 0;
        var queued = 0;
        var store = new ChunkSaveStore(_world);
        store.MoveFile = (tmp, final) =>
        {
            if (final.EndsWith(WorldStore.ChunkFileName(0, 0, 0), StringComparison.Ordinal))
            {
                entered.Set();
                release.Wait();
            }
            else if (final.EndsWith(WorldStore.ChunkFileName(1, 0, 0), StringComparison.Ordinal))
            {
                Interlocked.Increment(ref targetWrites);
            }
            File.Move(tmp, final, overwrite: true);
        };

        var newest = Snapshot("dirt", seed: 0);
        try
        {
            store.Enqueue(blockerKey, Snapshot("stone", seed: 100));
            Assert.True(entered.Wait(ShutdownTimeout), "writer did not reach the blocked move");
            for (var i = 1; i <= 100; i++)
            {
                newest = Snapshot("dirt", seed: i);
                store.Enqueue(key, newest);
                if (i == 50)
                    store.EnqueueFile("lod-edits.bin", [4, 5, 6]);
            }

            Assert.True(store.TryLoad(key, out var pending));
            AssertSameData(newest, pending);
            Assert.Equal(new byte[] { 4, 5, 6 }, store.ReadFile("lod-edits.bin"));
            queued = store.QueuedCount;
        }
        finally
        {
            release.Set();
            ShutdownOrFail(store);
        }

        Assert.Equal(1, targetWrites);
        Assert.Equal(2, queued);
        var reopened = new ChunkSaveStore(_world);
        try
        {
            Assert.True(reopened.TryLoad(key, out var disk));
            AssertSameData(newest, disk);
            Assert.Equal(new byte[] { 4, 5, 6 }, reopened.ReadFile("lod-edits.bin"));
        }
        finally
        {
            ShutdownOrFail(reopened);
        }
    }

    [Fact]
    public void NewEnqueueDuringAnInFlightWriteGetsAnotherTurn()
    {
        var key = ChunkKey.Pack(4, 0, 0);
        var first = Snapshot("stone", seed: 11);
        var newest = Snapshot("dirt", seed: 12);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var writes = 0;
        var store = new ChunkSaveStore(_world);
        store.MoveFile = (tmp, final) =>
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                entered.Set();
                release.Wait();
            }
            File.Move(tmp, final, overwrite: true);
        };
        try
        {
            store.Enqueue(key, first);
            Assert.True(entered.Wait(ShutdownTimeout));
            store.Enqueue(key, newest);
            Assert.Equal(1, store.QueuedCount);
            Assert.True(store.TryLoad(key, out var pending));
            AssertSameData(newest, pending);
        }
        finally
        {
            release.Set();
            ShutdownOrFail(store);
        }
        Assert.Equal(2, writes);
        var reopened = new ChunkSaveStore(_world);
        try
        {
            Assert.True(reopened.TryLoad(key, out var disk));
            AssertSameData(newest, disk);
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
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var attempts = 0;
        var store = new ChunkSaveStore(_world);
        Thread? shutdown = null;
        var shutdownResult = false;
        store.MoveFile = (_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.Set();
                Assert.True(release.Wait(ShutdownTimeout), "the test did not release the in-flight write");
            }
            throw new IOException("locked");
        };
        try
        {
            store.Enqueue(key, snapshot);
            Assert.True(entered.Wait(ShutdownTimeout), "the writer did not reach the first move");
            shutdown = new Thread(() => shutdownResult = store.Shutdown(ShutdownTimeout));
            shutdown.Start();
            Assert.True(SpinWait.SpinUntil(() => store.IsAddingCompletedForTests, ShutdownTimeout),
                "shutdown did not close admission while the write was in flight");
        }
        finally
        {
            release.Set();
            if (shutdown == null)
                ShutdownOrFail(store);
            else
                Assert.True(shutdown.Join(ShutdownTimeout), "shutdown did not return after the writer was released");
        }

        Assert.True(shutdownResult, "shutdown should join even when storage stays unavailable");
        Assert.Equal(2 * (SaveConfig.SaveMoveRetries + 1), Volatile.Read(ref attempts));
        Assert.Equal(1, store.FailedCount);
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
            if (failures <= SaveConfig.SaveMoveRetries)
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AWriteFirstFailingAfterShutdownClosesAdmissionGetsOneFinalTry(bool wholeFile, bool enqueueAfterClose)
    {
        var key = ChunkKey.Pack(2, 1, 3);
        var snapshot = Snapshot("stone", seed: 31);
        var rejected = Snapshot("dirt", seed: 32);
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var rejectedBytes = new byte[] { 7, 8, 9 };
        var initialCycleAttempts = SaveConfig.SaveMoveRetries + 1;
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var attempts = 0;
        var store = new ChunkSaveStore(_world);
        Thread? shutdown = null;
        var shutdownResult = false;
        store.MoveFile = (tmp, final) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt == 1)
            {
                entered.Set();
                Assert.True(release.Wait(ShutdownTimeout), "the test did not release the in-flight write");
            }
            if (attempt <= initialCycleAttempts)
                throw new IOException("first write cycle locked");
            File.Move(tmp, final, overwrite: true);
        };

        try
        {
            if (wholeFile)
                store.EnqueueFile("lod-edits.bin", bytes);
            else
                store.Enqueue(key, snapshot);
            Assert.True(entered.Wait(ShutdownTimeout), "the writer did not reach the first move");
            shutdown = new Thread(() => shutdownResult = store.Shutdown(ShutdownTimeout));
            shutdown.Start();
            Assert.True(SpinWait.SpinUntil(() => store.IsAddingCompletedForTests, ShutdownTimeout),
                "shutdown did not close admission while the first write was in flight");
            if (enqueueAfterClose)
            {
                if (wholeFile)
                    store.EnqueueFile("lod-edits.bin", rejectedBytes);
                else
                    store.Enqueue(key, rejected);
            }
        }
        finally
        {
            release.Set();
            if (shutdown == null)
                ShutdownOrFail(store);
            else
                Assert.True(shutdown.Join(ShutdownTimeout), "shutdown did not return after the writer was released");
        }

        Assert.True(shutdownResult, "the writer did not finish after the first failure");
        var reopened = new ChunkSaveStore(_world);
        try
        {
            if (wholeFile)
                Assert.Equal(bytes, reopened.ReadFile("lod-edits.bin"));
            else
            {
                Assert.True(reopened.TryLoad(key, out var disk));
                AssertSameData(snapshot, disk);
            }
        }
        finally
        {
            ShutdownOrFail(reopened);
        }
        Assert.Equal(initialCycleAttempts + 1, Volatile.Read(ref attempts));
        Assert.Equal(0, store.FailedCount);
        if (wholeFile && enqueueAfterClose)
            Assert.Equal(rejectedBytes, store.ReadFile("lod-edits.bin"));
        if (!wholeFile)
        {
            Assert.Equal(enqueueAfterClose ? 1 : 0, store.PendingCount);
            if (enqueueAfterClose)
            {
                Assert.True(store.TryLoad(key, out var pending));
                AssertSameData(rejected, pending);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualRetryAfterAdmissionClosesPreservesFailedWriteForShutdown(bool wholeFile)
    {
        var failedKey = ChunkKey.Pack(4, 1, 2);
        var blockerKey = ChunkKey.Pack(5, 1, 2);
        var acceptedSnapshot = Snapshot("stone", seed: 41);
        var blockerSnapshot = Snapshot("dirt", seed: 42);
        var acceptedBytes = Enumerable.Range(1, 64).Select(i => (byte)i).ToArray();
        var blockerBytes = Enumerable.Range(65, 64).Select(i => (byte)i).ToArray();
        var failedFile = "lod-edits.bin";
        var blockerFile = Path.Combine(WorldStore.PlayersDirectoryName, "retry-blocker.bin");
        var failedPath = Path.GetFullPath(wholeFile
            ? Path.Combine(_world, failedFile)
            : Path.Combine(ChunksDir, WorldStore.ChunkFileName(4, 1, 2)));
        var blockerPath = Path.GetFullPath(wholeFile
            ? Path.Combine(_world, blockerFile)
            : Path.Combine(ChunksDir, WorldStore.ChunkFileName(5, 1, 2)));
        var initialMoveAttempts = SaveConfig.SaveMoveRetries + 1;
        using var blockerEntered = new ManualResetEventSlim(false);
        using var releaseBlocker = new ManualResetEventSlim(false);
        var failedAttempts = 0;
        var blockerAttempts = 0;
        var store = new ChunkSaveStore(_world);
        Thread? shutdown = null;
        var shutdownResult = false;
        var retryCount = -1;
        var failedCountAfterRetry = -1;

        store.MoveFile = (tmp, final) =>
        {
            var path = Path.GetFullPath(final);
            if (path == failedPath)
            {
                if (Interlocked.Increment(ref failedAttempts) <= initialMoveAttempts)
                    throw new IOException("the accepted artifact fails its initial write cycle");
                File.Move(tmp, final, overwrite: true);
                return;
            }

            if (path == blockerPath)
            {
                Interlocked.Increment(ref blockerAttempts);
                blockerEntered.Set();
                Assert.True(releaseBlocker.Wait(ShutdownTimeout), "the test did not release the in-flight blocker write");
            }

            File.Move(tmp, final, overwrite: true);
        };

        try
        {
            if (wholeFile)
            {
                store.EnqueueFile(failedFile, acceptedBytes);
                store.EnqueueFile(blockerFile, blockerBytes);
            }
            else
            {
                store.Enqueue(failedKey, acceptedSnapshot);
                store.Enqueue(blockerKey, blockerSnapshot);
            }

            Assert.True(blockerEntered.Wait(ShutdownTimeout), "the writer did not reach the accepted blocker job");
            Assert.Equal(initialMoveAttempts, Volatile.Read(ref failedAttempts));
            Assert.Equal(1, store.FailedCount);

            shutdown = new Thread(() => shutdownResult = store.Shutdown(ShutdownTimeout));
            shutdown.Start();
            Assert.True(SpinWait.SpinUntil(() => store.IsAddingCompletedForTests, ShutdownTimeout),
                "shutdown did not close admission while the blocker write was in flight");

            retryCount = store.RetryFailed();
            failedCountAfterRetry = store.FailedCount;
        }
        finally
        {
            releaseBlocker.Set();
            if (shutdown == null)
                ShutdownOrFail(store);
            else
                Assert.True(shutdown.Join(ShutdownTimeout), "shutdown did not return after the blocker was released");
        }

        Assert.True(shutdownResult, "the writer did not finish after the blocker was released");
        var reopened = new ChunkSaveStore(_world);
        try
        {
            if (wholeFile)
            {
                Assert.Equal(acceptedBytes, reopened.ReadFile(failedFile));
                Assert.Equal(blockerBytes, reopened.ReadFile(blockerFile));
            }
            else
            {
                Assert.True(reopened.TryLoad(failedKey, out var acceptedOnDisk));
                AssertSameData(acceptedSnapshot, acceptedOnDisk);
                Assert.True(reopened.TryLoad(blockerKey, out var blockerOnDisk));
                AssertSameData(blockerSnapshot, blockerOnDisk);
            }
        }
        finally
        {
            ShutdownOrFail(reopened);
        }

        Assert.Equal(0, retryCount);
        Assert.Equal(1, failedCountAfterRetry);
        Assert.Equal(0, store.FailedCount);
        Assert.Equal(initialMoveAttempts + 1, Volatile.Read(ref failedAttempts));
        Assert.Equal(1, Volatile.Read(ref blockerAttempts));
    }

    [Fact]
    public void AChunkArchiveFailureKeepsTheOriginalAndRetriesThePendingSnapshot()
    {
        var key = ChunkKey.Pack(7, 1, 7);
        var path = Path.Combine(ChunksDir, WorldStore.ChunkFileName(7, 1, 7));
        Directory.CreateDirectory(ChunksDir);
        byte[] original = [1, 2, 3, 4, 5];
        File.WriteAllBytes(path, original);

        var backupBlocker = path + ".bad";
        var firstBarrier = Path.Combine(_world, "archive-failure-barrier.bin");
        var retryBarrier = Path.Combine(_world, "archive-retry-barrier.bin");
        using var firstDone = new ManualResetEventSlim(false);
        using var retryDone = new ManualResetEventSlim(false);
        var store = new ChunkSaveStore(_world);
        store.MoveFile = (tmp, final) =>
        {
            File.Move(tmp, final, overwrite: true);
            if (string.Equals(final, firstBarrier, StringComparison.Ordinal))
                firstDone.Set();
            else if (string.Equals(final, retryBarrier, StringComparison.Ordinal))
                retryDone.Set();
        };
        var replacement = Snapshot("dirt", seed: 71);

        try
        {
            Assert.False(store.TryLoad(key, out _));
            Directory.CreateDirectory(backupBlocker);

            store.Enqueue(key, replacement);
            store.EnqueueFile("archive-failure-barrier.bin", [8]);
            Assert.True(firstDone.Wait(ShutdownTimeout), "writer did not finish the blocked-archive job");

            // The chunk remains pending and the original stays at its final path while .bad is blocked.
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.True(store.TryLoad(key, out var pending));
            AssertSameData(replacement, pending);
            Assert.Equal(1, store.FailedCount);
            Assert.Equal(1, store.PendingCount);

            Directory.Delete(backupBlocker);
            Assert.Equal(1, store.RetryFailed());
            store.EnqueueFile("archive-retry-barrier.bin", [9]);
            Assert.True(retryDone.Wait(ShutdownTimeout), "writer did not finish the archival retry");

            Assert.Equal(original, File.ReadAllBytes(path + ".bad"));
            Assert.True(store.TryLoad(key, out var written));
            AssertSameData(replacement, written);
            Assert.Equal(0, store.FailedCount);
            Assert.Equal(0, store.PendingCount);
        }
        finally
        {
            if (Directory.Exists(backupBlocker))
                Directory.Delete(backupBlocker, recursive: true);
            ShutdownOrFail(store);
        }

        var reopened = new ChunkSaveStore(_world);
        try
        {
            Assert.True(reopened.TryLoad(key, out var persisted));
            AssertSameData(replacement, persisted);
            Assert.Equal(original, File.ReadAllBytes(path + ".bad"));
        }
        finally
        {
            ShutdownOrFail(reopened);
        }
    }

    [Fact]
    public void AWholeFileArchiveFailureKeepsTheOriginalAndRetriesThePendingBytes()
    {
        var fileName = "players/Alice.json";
        var final = Path.Combine(_world, fileName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        byte[] original = [0xFF, 0x00, 0x7B, 0x13];
        byte[] replacement = [9, 8, 7, 6];
        File.WriteAllBytes(final, original);

        var backupBlocker = final + ".bad";
        var firstBarrier = Path.Combine(_world, "whole-file-archive-failure-barrier.bin");
        var retryBarrier = Path.Combine(_world, "whole-file-archive-retry-barrier.bin");
        using var firstDone = new ManualResetEventSlim(false);
        using var retryDone = new ManualResetEventSlim(false);
        var store = new ChunkSaveStore(_world);
        store.MoveFile = (tmp, destination) =>
        {
            File.Move(tmp, destination, overwrite: true);
            if (string.Equals(destination, firstBarrier, StringComparison.Ordinal))
                firstDone.Set();
            else if (string.Equals(destination, retryBarrier, StringComparison.Ordinal))
                retryDone.Set();
        };

        try
        {
            // A real exclusive share makes the store's ordinary read path fail and mark this file.
            using (var exclusive = new FileStream(final, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Null(store.ReadFile(fileName));

            Directory.CreateDirectory(backupBlocker);
            store.EnqueueFile(fileName, replacement);
            store.EnqueueFile("whole-file-archive-failure-barrier.bin", [8]);
            Assert.True(firstDone.Wait(ShutdownTimeout), "writer did not finish the blocked-archive job");

            // The new payload is still accepted/readable, while the original remains untouched on disk.
            Assert.Equal(original, File.ReadAllBytes(final));
            Assert.Equal(replacement, store.ReadFile(fileName));
            Assert.Equal(1, store.FailedCount);

            Directory.Delete(backupBlocker);
            Assert.Equal(1, store.RetryFailed());
            store.EnqueueFile("whole-file-archive-retry-barrier.bin", [9]);
            Assert.True(retryDone.Wait(ShutdownTimeout), "writer did not finish the archival retry");

            Assert.Equal(original, File.ReadAllBytes(final + ".bad"));
            Assert.Equal(replacement, File.ReadAllBytes(final));
            Assert.Equal(0, store.FailedCount);
            Assert.Equal(replacement, store.ReadFile(fileName));
        }
        finally
        {
            if (Directory.Exists(backupBlocker))
                Directory.Delete(backupBlocker, recursive: true);
            ShutdownOrFail(store);
        }

        var reopened = new ChunkSaveStore(_world);
        try
        {
            Assert.Equal(replacement, reopened.ReadFile(fileName));
            Assert.Equal(original, File.ReadAllBytes(final + ".bad"));
        }
        finally
        {
            ShutdownOrFail(reopened);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArchiveFailureAtShutdownRetriesAcceptedPayloadAfterRejectedLateEnqueue(bool wholeFile)
    {
        var fileName = "players/ArchiveRace.json";
        var key = ChunkKey.Pack(8, 1, 8);
        var final = wholeFile
            ? Path.Combine(_world, fileName.Replace('/', Path.DirectorySeparatorChar))
            : Path.Combine(ChunksDir, WorldStore.ChunkFileName(8, 1, 8));
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        byte[] original = [0xFF, 0x00, 0x21, 0x7B];
        File.WriteAllBytes(final, original);
        var backupBlocker = final + ".bad";
        var barrier = Path.Combine(_world, "archive-shutdown-barrier.bin");
        using var atBarrier = new ManualResetEventSlim(false);
        using var releaseBarrier = new ManualResetEventSlim(false);
        var acceptedSnapshot = Snapshot("stone", seed: 81);
        var rejectedSnapshot = Snapshot("dirt", seed: 82);
        byte[] acceptedBytes = [4, 3, 2, 1];
        byte[] rejectedBytes = [1, 2, 3, 4];
        var store = new ChunkSaveStore(_world);
        store.MoveFile = (tmp, destination) =>
        {
            File.Move(tmp, destination, overwrite: true);
            if (!string.Equals(destination, barrier, StringComparison.Ordinal))
                return;
            atBarrier.Set();
            if (!releaseBarrier.Wait(ShutdownTimeout))
                throw new TimeoutException("test did not release the writer barrier");
        };
        Thread? shutdown = null;
        var shutdownResult = false;
        try
        {
            if (wholeFile)
            {
                using var exclusive = new FileStream(final, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.Null(store.ReadFile(fileName));
                store.EnqueueFile(fileName, acceptedBytes);
            }
            else
            {
                Assert.False(store.TryLoad(key, out _));
                store.Enqueue(key, acceptedSnapshot);
            }
            Directory.CreateDirectory(backupBlocker);
            store.EnqueueFile("archive-shutdown-barrier.bin", [8]);
            Assert.True(atBarrier.Wait(ShutdownTimeout), "writer did not reach the barrier after archive failure");
            Assert.Equal(1, store.FailedCount);
            Assert.Equal(original, File.ReadAllBytes(final));

            shutdown = new Thread(() => shutdownResult = store.Shutdown(ShutdownTimeout));
            shutdown.Start();
            Assert.True(SpinWait.SpinUntil(() => store.IsAddingCompletedForTests, ShutdownTimeout),
                "shutdown did not close admission while the writer was blocked");

            // Enqueue publishes to memory before admission rejects its job; the close-time retry
            // must still use the exact payload from the accepted attempt above.
            if (wholeFile)
            {
                store.EnqueueFile(fileName, rejectedBytes);
                Assert.Equal(rejectedBytes, store.ReadFile(fileName));
            }
            else
            {
                store.Enqueue(key, rejectedSnapshot);
                Assert.True(store.TryLoad(key, out var pending));
                AssertSameData(rejectedSnapshot, pending);
            }

            Directory.Delete(backupBlocker);
            releaseBarrier.Set();
        }
        finally
        {
            releaseBarrier.Set();
            if (Directory.Exists(backupBlocker))
                Directory.Delete(backupBlocker, recursive: true);
            if (shutdown == null)
                ShutdownOrFail(store);
            else
                Assert.True(shutdown.Join(ShutdownTimeout), "shutdown did not finish the close-time retry");
        }

        Assert.True(shutdownResult, "writer should join after the final accepted-payload retry");
        Assert.Equal(original, File.ReadAllBytes(final + ".bad"));
        if (wholeFile)
            Assert.Equal(acceptedBytes, File.ReadAllBytes(final));
        else
        {
            var reopened = new ChunkSaveStore(_world);
            try
            {
                Assert.True(reopened.TryLoad(key, out var persisted));
                AssertSameData(acceptedSnapshot, persisted);
            }
            finally
            {
                ShutdownOrFail(reopened);
            }
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

    /// <summary>
    /// A chunk file from an older format version (a world saved by an older build) is not misread:
    /// the chunk regenerates for the session, and the old file is set aside as .bad, untouched,
    /// before the first write of the chunk replaces it.
    /// </summary>
    [Fact]
    public void AnOlderVersionChunkFileRegeneratesAndIsSetAside()
    {
        var key = ChunkKey.Pack(6, 0, -6);
        var path = Path.Combine(ChunksDir, WorldStore.ChunkFileName(6, 0, -6));
        Directory.CreateDirectory(ChunksDir);
        byte[] older;
        using (var encoded = new MemoryStream())
        {
            ChunkSaveCodec.Write(encoded, Snapshot("stone", seed: 5), static id => BlockType.ById[id]?.Name);
            older = encoded.ToArray();
        }
        // The version byte follows the four magic bytes
        older[4] = ChunkSaveCodec.Version - 1;
        File.WriteAllBytes(path, older);

        var store = new ChunkSaveStore(_world);
        try
        {
            Assert.True(store.IsIndexed(key));
            Assert.False(store.TryLoad(key, out _));

            var regenerated = Snapshot("dirt", seed: 6);
            store.Enqueue(key, regenerated);
            ShutdownOrFail(store);

            Assert.Equal(older, File.ReadAllBytes(path + ".bad"));
            Assert.True(store.TryLoad(key, out var written));
            AssertSameData(regenerated, written);
        }
        finally
        {
            store.Shutdown(ShutdownTimeout);
        }
    }
}
