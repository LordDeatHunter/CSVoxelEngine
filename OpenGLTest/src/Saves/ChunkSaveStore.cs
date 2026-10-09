using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;

namespace silkgltriangle;

/// <summary>
/// One world's chunk files (<c>&lt;world&gt;/chunks/&lt;cx&gt;_&lt;cy&gt;_&lt;cz&gt;.bin</c>, see
/// <see cref="ChunkSaveCodec"/>) plus a few whole-file side cars in the world folder
/// (<c>lod-edits.bin</c>). GL-free; <see cref="Log"/> is used only for warnings, never for
/// correctness.
/// <para>
/// Writes are asynchronous, in the shape of <see cref="Log"/>: <see cref="Enqueue"/> and
/// <see cref="EnqueueFile"/> never block, and one background thread ("ChunkSaveWriter") encodes and
/// writes the jobs in order. The main thread only ever copies arrays
/// (<see cref="Chunk.TakeSnapshot"/>); palette building, Deflate and the disk all happen here.
/// </para>
/// <para>
/// Ordering is what keeps an unloaded chunk readable while its write is still in flight:
/// <list type="number">
/// <item><see cref="Enqueue"/> publishes the snapshot in the pending map BEFORE the job is queued
/// (and the chunk manager enqueues BEFORE it forgets the chunk), so a chunk that streams straight
/// back in is restored from memory.</item>
/// <item>The writer writes <c>.bin.tmp</c>, moves it over the final file (retried: a reader or a
/// virus scanner may hold it for a moment), adds the key to the on-disk index and only THEN
/// removes the pending entry — and only if it is still the snapshot it wrote, so a newer enqueue
/// for the same key survives. A lookup therefore always finds the chunk in one of the two.</item>
/// <item>A write that finally fails keeps its pending entry and is remembered as failed: the chunk
/// stays readable for the rest of the session, the next flush of that chunk retries it, and
/// <see cref="Shutdown"/> re-queues every failed write once more before the writer stops, so an
/// edit never silently misses the disk because of a moment's lock.</item>
/// <item>A file that cannot be read (after a few retries, or because it is corrupt) is never
/// overwritten in place: the writer moves it to <c>.bad</c> first, so what was there can still be
/// recovered by hand instead of being replaced by a regenerated chunk or an empty player file.</item>
/// </list>
/// The index is built once at open from the files on disk, so a generation worker never probes the
/// file system for a chunk that was never saved. Reads open with
/// <see cref="FileShare.Delete"/> so they never block the writer's replace.
/// </para>
/// <para>
/// One world folder has at most one live writer: the store holds <c>session.lock</c> open with
/// <see cref="FileShare.None"/> from construction until the writer thread exits, so opening the same
/// folder again — from this process after a writer that overran its shutdown timeout, or from a
/// second process such as the dedicated server — throws <see cref="IOException"/> instead of letting
/// two writers race over the same files.
/// </para>
/// </summary>
public sealed class ChunkSaveStore
{
    private const string TempExtension = ".tmp";
    private const string UnreadableExtension = ".bad";
    /// <summary>The lock file a live store holds in its world folder.</summary>
    public const string LockFileName = "session.lock";

    // Exactly one of Chunk (a chunk job, keyed by Key) or FileName + Bytes (a whole-file job) is set
    private sealed record SaveJob(long Key, ChunkSnapshot? Chunk, string? FileName, byte[]? Bytes);

    private readonly ConcurrentDictionary<long, ChunkSnapshot> _pending = new();
    private readonly ConcurrentDictionary<string, byte[]> _pendingFiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, byte> _index = new();
    // Writes that could not land (kept pending), retried by RetryFailed and at Shutdown
    private readonly ConcurrentDictionary<long, byte> _failedChunks = new();
    private readonly ConcurrentDictionary<string, byte> _failedFiles = new(StringComparer.Ordinal);
    // Files a read gave up on: set aside as .bad before the writer first replaces them
    private readonly ConcurrentDictionary<long, byte> _unreadableChunks = new();
    private readonly ConcurrentDictionary<string, byte> _unreadableFiles = new(StringComparer.Ordinal);
    private readonly BlockingCollection<SaveJob> _jobs = new();
    private readonly Thread _writer;
    private readonly FileStream _lock;

    /// <summary>Opens (creating when absent) the chunk store of the world in
    /// <paramref name="worldDirectory"/>: takes the session lock, removes the temp files a crash
    /// mid-write left behind, indexes the saved chunks and starts the writer thread. Throws
    /// <see cref="IOException"/> when another store (this process or another) still holds the folder.</summary>
    public ChunkSaveStore(string worldDirectory)
    {
        WorldDirectory = Path.GetFullPath(worldDirectory);
        ChunksDirectory = Path.Combine(WorldDirectory, WorldStore.ChunksDirectoryName);
        Directory.CreateDirectory(ChunksDirectory);

        // Taken before anything is touched: the leftover-temp sweep below would otherwise delete
        // the temp file of a writer that is still running over this folder.
        var lockPath = Path.Combine(WorldDirectory, LockFileName);
        try
        {
            _lock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException e)
        {
            throw new IOException($"The world folder '{WorldDirectory}' is still being written by another session: {e.Message}", e);
        }

        DeleteLeftoverTemps(WorldDirectory);
        DeleteLeftoverTemps(ChunksDirectory);
        BuildIndex();

        _writer = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "ChunkSaveWriter"
        };
        _writer.Start();
    }

    /// <summary>The world folder whole-file jobs are written into.</summary>
    public string WorldDirectory { get; }

    /// <summary>The folder the chunk files live in.</summary>
    public string ChunksDirectory { get; }

    /// <summary>Moves a finished temp file over its final name. A fault-injection seam for the
    /// tests; the writer retries it on <see cref="IOException"/>.</summary>
    internal Action<string, string> MoveFile { get; set; } = (tmp, final) => File.Move(tmp, final, overwrite: true);

    /// <summary>Chunk snapshots enqueued but not yet (successfully) on disk.</summary>
    internal int PendingCount => _pending.Count;

    /// <summary>Writes (chunks and files) whose last attempt could not land on disk.</summary>
    public int FailedCount => _failedChunks.Count + _failedFiles.Count;

    /// <summary>Whether a chunk file for <paramref name="key"/> is known to be on disk.</summary>
    internal bool IsIndexed(long key) => _index.ContainsKey(key);

    /// <summary>
    /// Queues <paramref name="snapshot"/> to be written as the chunk at <paramref name="key"/>
    /// (<see cref="ChunkKey.Pack(int, int, int)"/>). Never blocks. The snapshot is readable through
    /// <see cref="TryLoad"/> from the moment this returns, and a later enqueue for the same key
    /// replaces it. After <see cref="Shutdown"/> the write is dropped with a warning (the snapshot
    /// stays readable in memory).
    /// </summary>
    public void Enqueue(long key, ChunkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Publish first: the caller forgets the chunk right after this returns
        _pending[key] = snapshot;
        TryAdd(new SaveJob(key, snapshot, null, null));
    }

    /// <summary>
    /// The saved state of the chunk at <paramref name="key"/>: the newest pending snapshot when a
    /// write is still in flight, else the file on disk. False when the chunk was never saved or
    /// its file cannot be read (warned; the chunk then regenerates). Safe from any thread — the
    /// generation workers call it for every chunk they create, so a miss never touches the disk.
    /// </summary>
    public bool TryLoad(long key, [MaybeNullWhen(false)] out ChunkSnapshot snapshot)
    {
        if (_pending.TryGetValue(key, out var pending))
        {
            snapshot = pending;
            return true;
        }

        snapshot = null;
        if (!_index.ContainsKey(key))
            return false;

        var path = ChunkPath(key);
        // A read can lose a race with a scanner or a backup holding the file for a moment; a
        // transient failure must not become a regenerated chunk, so it is retried like the move.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                snapshot = ChunkSaveCodec.Read(stream,
                    static name => ContentRegistry.TryResolveBlock(name, out var id) ? id : null);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < WorldGenerationConfig.SaveMoveRetries)
            {
                Thread.Sleep(WorldGenerationConfig.SaveMoveRetryDelayMs);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Regenerate for this session, but never write over what is there: the writer
                // sets the file aside as .bad first (see WriteChunk).
                Log.Warn($"Could not read saved chunk '{path}', regenerating it: {e.Message}");
                _unreadableChunks.TryAdd(key, 0);
                snapshot = null;
                return false;
            }
        }
    }

    /// <summary>
    /// Queues <paramref name="bytes"/> to be written as <paramref name="fileName"/> in the world
    /// folder (a plain file name, no directories). Never blocks; the bytes are owned by the store
    /// from here on and must not be modified. <see cref="ReadFile"/> sees them at once.
    /// </summary>
    public void EnqueueFile(string fileName, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ValidateFileName(fileName);
        _pendingFiles[fileName] = bytes;
        TryAdd(new SaveJob(0, null, fileName, bytes));
    }

    /// <summary>The contents of <paramref name="fileName"/> in the world folder (the newest pending
    /// write when one is in flight), or null when it does not exist or cannot be read (warned).</summary>
    public byte[]? ReadFile(string fileName)
    {
        ValidateFileName(fileName);
        if (_pendingFiles.TryGetValue(fileName, out var pending))
            return pending;

        var path = Path.Combine(WorldDirectory, fileName);
        if (!File.Exists(path))
            return null;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                return bytes;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < WorldGenerationConfig.SaveMoveRetries)
            {
                Thread.Sleep(WorldGenerationConfig.SaveMoveRetryDelayMs);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The caller treats null as "no save" and will write a fresh one; keep the old
                // bytes as .bad rather than letting that fresh write replace them.
                Log.Warn($"Could not read save file '{path}': {e.Message}");
                _unreadableFiles.TryAdd(fileName, 0);
                return null;
            }
        }
    }

    /// <summary>
    /// Re-queues every write whose last attempt failed (its newest pending snapshot or bytes), so a
    /// file that was briefly locked gets another try without the chunk having to be edited again.
    /// Returns how many were re-queued. Shutdown calls it once before the writer stops.
    /// </summary>
    public int RetryFailed()
    {
        var retried = 0;
        foreach (var key in _failedChunks.Keys)
        {
            if (_failedChunks.TryRemove(key, out _) && _pending.TryGetValue(key, out var snapshot))
            {
                TryAdd(new SaveJob(key, snapshot, null, null));
                retried++;
            }
        }
        foreach (var fileName in _failedFiles.Keys)
        {
            if (_failedFiles.TryRemove(fileName, out _) && _pendingFiles.TryGetValue(fileName, out var bytes))
            {
                TryAdd(new SaveJob(0, null, fileName, bytes));
                retried++;
            }
        }
        return retried;
    }

    /// <summary>
    /// Stops accepting jobs and waits up to <paramref name="timeout"/> for the writer to finish the
    /// ones already queued. Returns false (warned) when it did not finish in time; the writer is a
    /// background thread, so it never keeps the process alive. Reads keep working afterwards.
    /// A store handed to a <see cref="World"/> is shut down by <see cref="World.SaveAndDispose"/>:
    /// the World owns it, so its caller must not shut it down again and gives the next World a
    /// new store over the same folder.
    /// </summary>
    public bool Shutdown(TimeSpan timeout)
    {
        if (!_jobs.IsAddingCompleted)
        {
            // One more go at whatever could not land earlier in the session: the writer drains
            // the queue in order, so these run after every flush already queued ahead of them.
            var retried = RetryFailed();
            if (retried > 0)
                Log.Warn($"Chunk save store for '{WorldDirectory}': retrying {retried} failed write(s) at shutdown.");
            _jobs.CompleteAdding();
        }
        if (_writer.Join(timeout))
            return true;

        Log.Warn($"Chunk save writer for '{WorldDirectory}' did not finish within {timeout.TotalSeconds:0.#} s; " +
                 $"{_jobs.Count} write(s) still queued. The folder stays locked until it finishes.");
        return false;
    }

    private void TryAdd(SaveJob job)
    {
        try
        {
            _jobs.Add(job);
        }
        catch (InvalidOperationException)
        {
            Log.Warn($"Chunk save store for '{WorldDirectory}' is shut down; a write was dropped.");
        }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var job in _jobs.GetConsumingEnumerable())
            {
                // The writer must survive anything a single job throws: dying would silently stop
                // every later save of the session.
                try
                {
                    if (job.Chunk != null)
                        WriteChunk(job.Key, job.Chunk);
                    else
                        WriteWholeFile(job.FileName!, job.Bytes!);
                }
                catch (Exception e)
                {
                    Log.Warn($"Chunk save writer: unexpected {e.GetType().Name}: {e.Message}");
                }
            }
        }
        finally
        {
            // The folder is free for the next session only once no write can still be in flight
            _lock.Dispose();
        }
    }

    private void WriteChunk(long key, ChunkSnapshot snapshot)
    {
        using var encoded = new MemoryStream();
        ChunkSaveCodec.Write(encoded, snapshot, static id => BlockType.ById[id]?.Name);

        var final = ChunkPath(key);
        if (_unreadableChunks.TryRemove(key, out _))
            SetAsideUnreadable(final);
        if (!TryWriteAndMove(final, encoded.GetBuffer().AsSpan(0, (int)encoded.Length)))
        {
            // Keep the pending entry: still readable, the next flush retries, and so does Shutdown
            _failedChunks.TryAdd(key, 0);
            return;
        }

        _failedChunks.TryRemove(key, out _);
        _index.TryAdd(key, 0);
        // Only if it is still THIS snapshot: a newer enqueue for the key must survive
        _pending.TryRemove(new KeyValuePair<long, ChunkSnapshot>(key, snapshot));
    }

    private void WriteWholeFile(string fileName, byte[] bytes)
    {
        var final = Path.Combine(WorldDirectory, fileName);
        if (_unreadableFiles.TryRemove(fileName, out _))
            SetAsideUnreadable(final);
        if (!TryWriteAndMove(final, bytes))
        {
            _failedFiles.TryAdd(fileName, 0);
            return;
        }
        _failedFiles.TryRemove(fileName, out _);
        _pendingFiles.TryRemove(new KeyValuePair<string, byte[]>(fileName, bytes));
    }

    // Keeps a file a read gave up on as <final>.bad (the previous .bad, if any, is replaced), so the
    // write about to replace it never destroys the only copy of what the player had.
    private static void SetAsideUnreadable(string final)
    {
        if (!File.Exists(final))
            return;
        try
        {
            File.Move(final, final + UnreadableExtension, overwrite: true);
            Log.Warn($"Kept the unreadable save file as '{final}{UnreadableExtension}'");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not set aside the unreadable save file '{final}': {e.Message}");
        }
    }

    // Writes <final>.tmp and moves it over <final>, retrying the move. False (warned) on failure.
    private bool TryWriteAndMove(string final, ReadOnlySpan<byte> bytes)
    {
        var tmp = final + TempExtension;
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                // Pushed to the disk before the rename, or a power cut could keep the rename and
                // lose the data, leaving an empty chunk file where a complete one used to be
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not write save file '{tmp}': {e.Message}");
            return false;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                MoveFile(tmp, final);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt < WorldGenerationConfig.SaveMoveRetries)
                {
                    Thread.Sleep(WorldGenerationConfig.SaveMoveRetryDelayMs);
                    continue;
                }

                Log.Warn($"Could not replace save file '{final}' after {attempt + 1} attempts, keeping it in memory: {e.Message}");
                return false;
            }
        }
    }

    private string ChunkPath(long key)
    {
        ChunkKey.Unpack(key, out var cx, out var cy, out var cz);
        return Path.Combine(ChunksDirectory, WorldStore.ChunkFileName(cx, cy, cz));
    }

    private void BuildIndex()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(ChunksDirectory, "*.bin"))
            {
                // The parser also rejects what the Windows wildcard over-matches (".binx")
                if (WorldStore.TryParseChunkFileName(Path.GetFileName(path), out var cx, out var cy, out var cz))
                    _index.TryAdd(ChunkKey.Pack(cx, cy, cz), 0);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not index saved chunks in '{ChunksDirectory}': {e.Message}");
        }
    }

    // A crash between writing a temp file and moving it leaves the temp behind; the final file
    // (if any) is still the previous complete write, so the temp is simply discarded.
    private static void DeleteLeftoverTemps(string directory)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*" + TempExtension))
            {
                if (!path.EndsWith(TempExtension, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    File.Delete(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Could not remove leftover save temp file '{path}': {e.Message}");
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not scan '{directory}' for leftover save temp files: {e.Message}");
        }
    }

    private static void ValidateFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        if (Path.GetFileName(fileName) != fileName || fileName is "." or "..")
            throw new ArgumentException($"'{fileName}' is not a plain file name.", nameof(fileName));
    }
}
