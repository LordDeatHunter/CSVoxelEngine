using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.Saves;

/// <summary>
/// One world's chunk files (<c>&lt;world&gt;/chunks/&lt;cx&gt;_&lt;cy&gt;_&lt;cz&gt;.bin</c>, see
/// <see cref="ChunkSaveCodec"/>) plus a few whole-file side cars in the world folder
/// (<c>lod-edits.bin</c>) and the hosted players' files in its
/// <see cref="WorldStore.PlayersDirectoryName"/> subfolder. GL-free; <see cref="Log"/> is used only
/// for warnings, never for correctness.
/// <para>
/// Writes are asynchronous, in the shape of <see cref="Log"/>: <see cref="Enqueue"/> and
/// <see cref="EnqueueFile"/> never block, and one background thread ("ChunkSaveWriter") encodes and
/// writes queued jobs in order. Waiting chunk jobs carry only a key and resolve its newest pending
/// snapshot when the writer reaches them; whole-file jobs retain their bytes. A later chunk
/// snapshot can thus be written before an intervening whole-file job, but each artifact's final
/// state is the last enqueued one after the writer drains. The main thread only ever copies arrays
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
/// stays readable for the rest of the session. A later enqueue, an explicit
/// <see cref="RetryFailed"/>, or <see cref="Shutdown"/> gives it another try. Shutdown drains
/// accepted jobs, then makes one final bounded write cycle for failed artifacts, including
/// failures first reported while closing.</item>
/// <item>A chunk that cannot be read or decoded, or a whole file that cannot be read, is never
/// overwritten in place: the writer moves it to <c>.bad</c> first. If archival fails, the original
/// stays at its final path and the exact accepted replacement remains available for retry.</item>
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

    // A chunk job carries only its key; a whole-file job carries its file name and bytes.
    private sealed record SaveJob(long Key, string? FileName, byte[]? Bytes);

    private readonly ConcurrentDictionary<long, ChunkSnapshot> _pending = new();
    // One waiting token per chunk key. Removed before the writer captures the snapshot, so an
    // enqueue during an in-flight write can always schedule the newer state.
    private readonly ConcurrentDictionary<long, byte> _queuedChunks = new();
    private readonly ConcurrentDictionary<string, byte[]> _pendingFiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, byte> _index = new();
    // Retain the last accepted write for each failed artifact: an enqueue after admission closes
    // may replace the readable pending value, but must not turn into a disk write at shutdown.
    private readonly ConcurrentDictionary<long, ChunkSnapshot> _failedChunks = new();
    private readonly ConcurrentDictionary<string, byte[]> _failedFiles = new(StringComparer.Ordinal);
    // Serializes manual retries with the writer's shutdown scan so a rejected queue add cannot
    // remove an artifact before that final scan sees it.
    private readonly object _failedRetryGate = new();
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
        var players = Path.Combine(WorldDirectory, WorldStore.PlayersDirectoryName);
        if (Directory.Exists(players))
            DeleteLeftoverTemps(players);
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

    /// <summary>Moves a finished temp file over its final name or archives an unreadable file.
    /// A fault-injection seam for the tests; replacement moves retry on <see cref="IOException"/>.</summary>
    internal Action<string, string> MoveFile { get; set; } = (tmp, final) => File.Move(tmp, final, overwrite: true);

    /// <summary>Chunk snapshots enqueued but not yet (successfully) on disk.</summary>
    internal int PendingCount => _pending.Count;

    /// <summary>Jobs waiting for the writer, excluding the one in flight.</summary>
    internal int QueuedCount => _jobs.Count;

    /// <summary>Whether shutdown has closed admission; used to synchronize writer fault tests.</summary>
    internal bool IsAddingCompletedForTests => _jobs.IsAddingCompleted;

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
        QueueChunk(key);
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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < SaveConfig.SaveMoveRetries)
            {
                Thread.Sleep(SaveConfig.SaveMoveRetryDelayMs);
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
    /// folder: a plain file name, or one under the <see cref="WorldStore.PlayersDirectoryName"/>
    /// subfolder (<c>players/&lt;name&gt;</c>, created on demand); no other directories. Never
    /// blocks; the bytes are owned by the store from here on and must not be modified.
    /// <see cref="ReadFile"/> sees them at once.
    /// </summary>
    public void EnqueueFile(string fileName, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ValidateFileName(fileName);
        _pendingFiles[fileName] = bytes;
        TryAdd(new SaveJob(0, fileName, bytes));
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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < SaveConfig.SaveMoveRetries)
            {
                Thread.Sleep(SaveConfig.SaveMoveRetryDelayMs);
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

    /// <summary>Marks a successfully read sidecar whose format parser rejected its bytes.</summary>
    internal void MarkUnreadableFile(string fileName)
    {
        ValidateFileName(fileName);
        _unreadableFiles.TryAdd(fileName, 0);
    }

    /// <summary>
    /// Re-queues every write whose last attempt failed (its newest pending snapshot or bytes), so a
    /// file that was briefly locked gets another try without the chunk having to be edited again.
    /// Returns how many retry jobs were newly accepted by the writer. Shutdown uses its own
    /// writer-side final pass after drain.
    /// </summary>
    public int RetryFailed()
    {
        lock (_failedRetryGate)
        {
            var retried = 0;
            foreach (var key in _failedChunks.Keys)
            {
                if (!_failedChunks.TryRemove(key, out var failedSnapshot))
                    continue;
                if (!_pending.ContainsKey(key))
                {
                    _failedChunks.TryAdd(key, failedSnapshot);
                    continue;
                }
                if (QueueChunk(key))
                    retried++;
                else
                    _failedChunks.TryAdd(key, failedSnapshot);
            }

            foreach (var fileName in _failedFiles.Keys)
            {
                if (!_failedFiles.TryRemove(fileName, out var failedBytes))
                    continue;
                var bytes = _pendingFiles.TryGetValue(fileName, out var pendingBytes) ? pendingBytes : failedBytes;
                if (TryAdd(new SaveJob(0, fileName, bytes)))
                    retried++;
                else
                    _failedFiles.TryAdd(fileName, failedBytes);
            }

            return retried;
        }
    }

    /// <summary>
    /// Stops accepting jobs and waits up to <paramref name="timeout"/> for the writer to finish the
    /// ones already queued and one final write cycle for failures. Returns false (warned) when
    /// the writer did not finish in time; true means joined, not that every write landed. The
    /// writer is a background thread, so it never keeps the process alive. Reads keep working.
    /// A store handed to a <see cref="World"/> is shut down by <see cref="World.SaveAndDispose"/>:
    /// the World owns it, so its caller must not shut it down again and gives the next World a
    /// new store over the same folder.
    /// </summary>
    public bool Shutdown(TimeSpan timeout)
    {
        if (!_jobs.IsAddingCompleted)
            _jobs.CompleteAdding();
        if (_writer.Join(timeout))
            return true;

        Log.Warn($"Chunk save writer for '{WorldDirectory}' did not finish within {timeout.TotalSeconds:0.#} s; " +
                 $"{_jobs.Count} write(s) still queued. The folder stays locked until it finishes.");
        return false;
    }

    private bool TryAdd(SaveJob job)
    {
        try
        {
            _jobs.Add(job);
            return true;
        }
        catch (InvalidOperationException)
        {
            Log.Warn($"Chunk save store for '{WorldDirectory}' is shut down; a write was dropped.");
            return false;
        }
    }

    private bool QueueChunk(long key)
    {
        if (!_queuedChunks.TryAdd(key, 0))
            return false;
        try
        {
            _jobs.Add(new SaveJob(key, null, null));
            return true;
        }
        catch (InvalidOperationException)
        {
            _queuedChunks.TryRemove(key, out _);
            Log.Warn($"Chunk save store for '{WorldDirectory}' is shut down; a write was dropped.");
            return false;
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
                    if (job.FileName == null)
                    {
                        _queuedChunks.TryRemove(job.Key, out _);
                        if (_pending.TryGetValue(job.Key, out var snapshot))
                            WriteChunk(job.Key, snapshot);
                    }
                    else
                        WriteWholeFile(job.FileName!, job.Bytes!);
                }
                catch (Exception e)
                {
                    Log.Warn($"Chunk save writer: unexpected {e.GetType().Name}: {e.Message}");
                }
            }
            RetryFailedAfterDrain();
        }
        finally
        {
            // The folder is free for the next session only once no write can still be in flight
            _lock.Dispose();
        }
    }

    // Only the writer runs this pass. Capture both worksets before writing either, so failures of
    // the final attempts stay pending/diagnosed but never schedule another shutdown cycle.
    private void RetryFailedAfterDrain()
    {
        var chunks = new List<KeyValuePair<long, ChunkSnapshot>>();
        var files = new List<KeyValuePair<string, byte[]>>();
        lock (_failedRetryGate)
        {
            foreach (var key in _failedChunks.Keys)
                if (_failedChunks.TryRemove(key, out var snapshot))
                    chunks.Add(new KeyValuePair<long, ChunkSnapshot>(key, snapshot));
            foreach (var fileName in _failedFiles.Keys)
                if (_failedFiles.TryRemove(fileName, out var bytes))
                    files.Add(new KeyValuePair<string, byte[]>(fileName, bytes));
        }

        if (chunks.Count + files.Count > 0)
            Log.Warn($"Chunk save store for '{WorldDirectory}': retrying {chunks.Count + files.Count} failed write(s) at shutdown.");

        foreach (var (key, snapshot) in chunks)
        {
            try
            {
                WriteChunk(key, snapshot);
            }
            catch (Exception e)
            {
                lock (_failedRetryGate)
                    _failedChunks.TryAdd(key, snapshot);
                Log.Warn($"Chunk save writer: unexpected {e.GetType().Name}: {e.Message}");
            }
        }

        foreach (var (fileName, bytes) in files)
        {
            try
            {
                WriteWholeFile(fileName, bytes);
            }
            catch (Exception e)
            {
                lock (_failedRetryGate)
                    _failedFiles.TryAdd(fileName, bytes);
                Log.Warn($"Chunk save writer: unexpected {e.GetType().Name}: {e.Message}");
            }
        }
    }

    private void WriteChunk(long key, ChunkSnapshot snapshot)
    {
        using var encoded = new MemoryStream();
        ChunkSaveCodec.Write(encoded, snapshot, static id => BlockType.ById[id]?.Name);

        var final = ChunkPath(key);
        if (_unreadableChunks.ContainsKey(key))
        {
            if (!SetAsideUnreadable(final))
            {
                lock (_failedRetryGate)
                    _failedChunks[key] = snapshot;
                return;
            }
            _unreadableChunks.TryRemove(key, out _);
        }
        if (!TryWriteAndMove(final, encoded.GetBuffer().AsSpan(0, (int)encoded.Length)))
        {
            // Keep the pending entry: still readable; a later enqueue or shutdown retries it.
            lock (_failedRetryGate)
                _failedChunks[key] = snapshot;
            return;
        }

        lock (_failedRetryGate)
            _failedChunks.TryRemove(key, out _);
        _index.TryAdd(key, 0);
        // Only if it is still THIS snapshot: a newer enqueue for the key must survive
        _pending.TryRemove(new KeyValuePair<long, ChunkSnapshot>(key, snapshot));
    }

    private void WriteWholeFile(string fileName, byte[] bytes)
    {
        var final = Path.Combine(WorldDirectory, fileName);
        if (_unreadableFiles.ContainsKey(fileName))
        {
            if (!SetAsideUnreadable(final))
            {
                lock (_failedRetryGate)
                    _failedFiles[fileName] = bytes;
                return;
            }
            _unreadableFiles.TryRemove(fileName, out _);
        }
        try
        {
            // A players/ file lands in a subfolder that a new world does not have yet
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not create the folder for save file '{final}': {e.Message}");
        }
        if (!TryWriteAndMove(final, bytes))
        {
            lock (_failedRetryGate)
                _failedFiles[fileName] = bytes;
            return;
        }
        lock (_failedRetryGate)
            _failedFiles.TryRemove(fileName, out _);
        _pendingFiles.TryRemove(new KeyValuePair<string, byte[]>(fileName, bytes));
    }

    // Keeps a file a read gave up on as <final>.bad (the previous .bad, if any, is replaced), so the
    // write about to replace it never destroys the only copy of what the player had.
    private bool SetAsideUnreadable(string final)
    {
        if (!File.Exists(final))
            return true;
        try
        {
            MoveFile(final, final + UnreadableExtension);
            Log.Warn($"Kept the unreadable save file as '{final}{UnreadableExtension}'");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not set aside the unreadable save file '{final}': {e.Message}");
            return false;
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
                if (attempt < SaveConfig.SaveMoveRetries)
                {
                    Thread.Sleep(SaveConfig.SaveMoveRetryDelayMs);
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

    // A plain file name, or "players/<plain file name>": the one subfolder whole files may use
    private static void ValidateFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        var prefix = WorldStore.PlayersDirectoryName + "/";
        var plain = fileName.StartsWith(prefix, StringComparison.Ordinal) ? fileName[prefix.Length..] : fileName;
        if (plain.Length == 0 || Path.GetFileName(plain) != plain || plain.Contains('/') || plain is "." or "..")
            throw new ArgumentException($"'{fileName}' is not a plain file name.", nameof(fileName));
    }
}
