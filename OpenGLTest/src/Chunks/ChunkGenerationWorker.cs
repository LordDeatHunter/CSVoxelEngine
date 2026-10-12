using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using VoxelEngine.Lod;

namespace VoxelEngine.Chunks;

/// <summary>
/// Handles continuous chunk generation in background threads, around every focus of a
/// <see cref="StreamingFocus"/> (the camera in singleplayer, each connected player on a server).
/// Each worker thread independently finds and generates the closest needed chunk around one focus,
/// taking the foci in turn, so every focus is served even while another still has work.
/// </summary>
public class ChunkGenerationWorker(ChunkManager chunkManager, StreamingFocus focus)
{
    private volatile bool _isRunning;

    // One frontier per focus index, shared by all MaxConcurrentBlockDataGenerations threads this
    // instance starts; see ChunkScanFrontier for the invariant the threads share (PERF_TODO C3).
    // Grown on demand (FrontiersFor) to the focus count and never shrunk: a slot whose focus moved
    // or now belongs to another player simply resets by center mismatch. Replaced, never mutated,
    // so a thread that read the array can index it without a lock.
    private ChunkScanFrontier[] _frontiers = [];

    // Which focus the next scan starts at, advanced by every call on every thread, so the foci
    // take turns being scanned first.
    private int _rotor;

    // Cached once: the scan calls this per offset, and a method group would allocate a delegate
    // per call. Same shape as the World.Update drop predicate (PERF_TODO C7).
    private readonly Func<Vector3, bool> _needsChunk = chunkManager.NeedsChunk;

    // The threads the last Start spawned, kept until a Stop has joined every one of them so
    // AnyThreadAlive stays truthful after a timed-out join and a later Stop can re-join.
    private Thread[]? _threads;

    // The workers' idle and error back-offs wait on this instead of Thread.Sleep, so Stop can cut
    // a sleeping worker's wait short and the join returns promptly instead of after a full poll.
    private readonly ManualResetEventSlim _wake = new(false);

    /// <summary>
    /// Whether any thread from the last <see cref="Start"/> is still running. False once
    /// <see cref="Stop"/> has returned true.
    /// </summary>
    internal bool AnyThreadAlive
    {
        get
        {
            var threads = _threads;
            if (threads == null) return false;
            foreach (var thread in threads)
                if (thread.IsAlive) return true;
            return false;
        }
    }

    /// <summary>
    /// Starts the chunk generation workers. Refuses (with a warning) while a thread from an
    /// earlier pool is still alive, which only happens after a <see cref="Stop"/> timed out:
    /// starting a second pool then would double the workers and leave the first pool's straggler
    /// reading worldgen statics the caller believes are quiet.
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;
        if (AnyThreadAlive)
        {
            Log.Warn("ChunkGen: Start refused, a worker from the previous pool is still running");
            return;
        }

        // A timed-out Stop leaves the event set; a new pool must start with it clear or every
        // idle worker would spin instead of waiting.
        _wake.Reset();
        _isRunning = true;

        var workerCount = WorldGenerationConfig.MaxConcurrentBlockDataGenerations;
        var threads = new Thread[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            threads[i] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"ChunkGen-{i}",
                Priority = ThreadPriority.BelowNormal
            };
        }

        // Published before any thread runs, so AnyThreadAlive can never miss a started worker.
        _threads = threads;
        foreach (var thread in threads)
            thread.Start();
    }

    /// <summary>
    /// Stops the chunk generation workers and joins them, each for at most
    /// <see cref="WorldGenerationConfig.WorkerStopJoinTimeoutMs"/>. A worker in the middle of
    /// <c>QueueChunk</c> finishes that chunk first; one waiting out its idle poll is woken.
    /// <para>
    /// True means every ChunkGen thread has exited, so none can read the worldgen statics any
    /// more: the precondition <see cref="WorldSeed.Apply"/> needs, together with
    /// <see cref="LodManager.Stop"/> and <see cref="ChunkManager.DrainMeshWork"/>. False means a
    /// thread outlived its join; the pool is kept so a later Stop can re-join it, and
    /// <see cref="WorldSeed.Apply"/> must NOT run. Calling it when <see cref="Start"/> never
    /// ran returns true.
    /// </para>
    /// </summary>
    public bool Stop()
    {
        _isRunning = false;
        _wake.Set();

        var threads = _threads;
        if (threads == null) return true;

        var allJoined = true;
        foreach (var thread in threads)
        {
            if (thread.Join(WorldGenerationConfig.WorkerStopJoinTimeoutMs)) continue;
            allJoined = false;
            Log.Warn($"ChunkGen: {thread.Name} did not stop within {WorldGenerationConfig.WorkerStopJoinTimeoutMs} ms");
        }

        if (!allJoined) return false;

        _threads = null;
        _wake.Reset();
        return true;
    }

    private void WorkerLoop()
    {
        while (_isRunning)
        {
            try
            {
                var pos = FindClosestNeededChunk();

                if (pos.HasValue)
                {
                    // QueueChunk generates block data synchronously and is guarded 
                    // against duplicate generation via _queuedChunkPositions
                    chunkManager.QueueChunk(pos.Value);
                }
                else
                {
                    _wake.Wait(WorldGenerationConfig.GenerationDelayIdleMs);
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine($"Error in chunk generation: {ex}");
                _wake.Wait(1000);
            }
        }
    }

    // Offsets within the view radius, sorted closest-first, so the scan below can
    // return at the first needed chunk instead of testing the whole 33^3 box. The scan no
    // longer restarts here on every call either: all of this instance's worker threads resume
    // from the shared per-focus ChunkScanFrontiers above (PERF_TODO C3). Internal so the tests can
    // build a frontier over the real array.
    internal static readonly Vector3[] SortedOffsets = BuildSortedOffsets();

    private static Vector3[] BuildSortedOffsets()
    {
        const int radius = WorldGenerationConfig.ChunkRadius;
        const int maxDistSq = WorldGenerationConfig.ChunkRadiusSq;

        var offsets = new List<(Vector3 offset, int distSq)>();

        for (int dx = -radius; dx <= radius; dx++)
        for (int dy = -radius; dy <= radius; dy++)
        for (int dz = -radius; dz <= radius; dz++)
        {
            var distSq = dx * dx + dy * dy + dz * dz;
            if (distSq <= maxDistSq)
                offsets.Add((new Vector3(dx, dy, dz), distSq));
        }

        offsets.Sort((a, b) => a.distSq.CompareTo(b.distSq));

        var result = new Vector3[offsets.Count];
        for (var i = 0; i < offsets.Count; i++)
            result[i] = offsets[i].offset;
        return result;
    }

    /// <summary>
    /// The closest chunk inside the view radius of one focus that is not loaded and not already
    /// claimed, or null if no focus has one (or there is no focus). The foci are tried in turn,
    /// starting one further along on every call, and the first focus with a needed chunk wins, so a
    /// focus whose sphere is complete never starves another. Internal so the tests can drive one
    /// scan without starting a thread.
    /// The foci and the removal version are both sampled once, up front, before any probe: a
    /// version bump that lands mid-scan is missed by this call and caught by the next, which is
    /// exactly what the pre-C3 full scan did with a chunk freed behind its read head. A later
    /// focus's frontier is probed after an earlier one's with that same early version sample, which
    /// is the same conservative case: a node it publishes for a stale version is discarded by the
    /// first reader that sampled the bump.
    /// </summary>
    internal Vector3? FindClosestNeededChunk()
    {
        var foci = focus.Chunks;
        var version = chunkManager.ChunkRemovalVersion;
        var count = foci.Length;
        if (count == 0) return null;

        var frontiers = FrontiersFor(count);
        var start = (int)((uint)Interlocked.Increment(ref _rotor) % (uint)count);
        for (var n = 0; n < count; n++)
        {
            var i = start + n;
            if (i >= count) i -= count;

            var next = frontiers[i].FindNext(foci[i], version, _needsChunk);
            if (next.HasValue) return next;
        }

        return null;
    }

    /// <summary>
    /// The frontier array, grown to at least <paramref name="count"/> slots. Every worker thread
    /// calls this, so the growth is a copy-and-swap: a thread allocates a longer copy with fresh
    /// frontiers in the new slots and publishes it only if nobody replaced the array meanwhile,
    /// re-reading on a lost race. The existing frontiers move into the copy by reference, so their
    /// published prefixes survive the growth.
    /// </summary>
    private ChunkScanFrontier[] FrontiersFor(int count)
    {
        while (true)
        {
            var current = Volatile.Read(ref _frontiers);
            if (current.Length >= count) return current;

            var grown = new ChunkScanFrontier[count];
            Array.Copy(current, grown, current.Length);
            for (var i = current.Length; i < count; i++)
                grown[i] = new ChunkScanFrontier(SortedOffsets);

            if (ReferenceEquals(Interlocked.CompareExchange(ref _frontiers, grown, current), current))
                return grown;
        }
    }
}

