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
/// <item>A write that finally fails keeps its pending entry: the chunk stays readable for the rest
/// of the session and the next flush of that chunk retries it.</item>
/// </list>
/// The index is built once at open from the files on disk, so a generation worker never probes the
/// file system for a chunk that was never saved. Reads open with
/// <see cref="FileShare.Delete"/> so they never block the writer's replace.
/// </para>
/// </summary>
public sealed class ChunkSaveStore
{
    private const string TempExtension = ".tmp";

    // Exactly one of Chunk (a chunk job, keyed by Key) or FileName + Bytes (a whole-file job) is set
    private sealed record SaveJob(long Key, ChunkSnapshot? Chunk, string? FileName, byte[]? Bytes);

    private readonly ConcurrentDictionary<long, ChunkSnapshot> _pending = new();
    private readonly ConcurrentDictionary<string, byte[]> _pendingFiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, byte> _index = new();
    private readonly BlockingCollection<SaveJob> _jobs = new();
    private readonly Thread _writer;

    /// <summary>Opens (creating when absent) the chunk store of the world in
    /// <paramref name="worldDirectory"/>: removes the temp files a crash mid-write left behind,
    /// indexes the saved chunks and starts the writer thread.</summary>
    public ChunkSaveStore(string worldDirectory)
    {
        WorldDirectory = Path.GetFullPath(worldDirectory);
        ChunksDirectory = Path.Combine(WorldDirectory, WorldStore.ChunksDirectoryName);
        Directory.CreateDirectory(ChunksDirectory);

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
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            snapshot = ChunkSaveCodec.Read(stream,
                static name => ContentRegistry.TryResolveBlock(name, out var id) ? id : null);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn($"Could not read saved chunk '{path}', regenerating it: {e.Message}");
            snapshot = null;
            return false;
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

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not read save file '{path}': {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Stops accepting jobs and waits up to <paramref name="timeout"/> for the writer to finish the
    /// ones already queued. Returns false (warned) when it did not finish in time; the writer is a
    /// background thread, so it never keeps the process alive. Reads keep working afterwards.
    /// </summary>
    public bool Shutdown(TimeSpan timeout)
    {
        _jobs.CompleteAdding();
        if (_writer.Join(timeout))
            return true;

        Log.Warn($"Chunk save writer for '{WorldDirectory}' did not finish within {timeout.TotalSeconds:0.#} s; " +
                 $"{_jobs.Count} write(s) still queued.");
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

    private void WriteChunk(long key, ChunkSnapshot snapshot)
    {
        using var encoded = new MemoryStream();
        ChunkSaveCodec.Write(encoded, snapshot, static id => BlockType.ById[id]?.Name);

        var final = ChunkPath(key);
        if (!TryWriteAndMove(final, encoded.GetBuffer().AsSpan(0, (int)encoded.Length)))
            return; // Keep the pending entry: still readable, and the next flush retries

        _index.TryAdd(key, 0);
        // Only if it is still THIS snapshot: a newer enqueue for the key must survive
        _pending.TryRemove(new KeyValuePair<long, ChunkSnapshot>(key, snapshot));
    }

    private void WriteWholeFile(string fileName, byte[] bytes)
    {
        if (!TryWriteAndMove(Path.Combine(WorldDirectory, fileName), bytes))
            return;
        _pendingFiles.TryRemove(new KeyValuePair<string, byte[]>(fileName, bytes));
    }

    // Writes <final>.tmp and moves it over <final>, retrying the move. False (warned) on failure.
    private bool TryWriteAndMove(string final, ReadOnlySpan<byte> bytes)
    {
        var tmp = final + TempExtension;
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                stream.Write(bytes);
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
