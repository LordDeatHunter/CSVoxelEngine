using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace silkgltriangle;

/// <summary>
/// Handles continuous chunk generation in background threads.
/// Each worker thread independently finds and generates the closest needed chunk.
/// </summary>
public class ChunkGenerationWorker(ChunkManager chunkManager, Camera camera)
{
    private volatile bool _isRunning;

    // One frontier for all MaxConcurrentBlockDataGenerations threads this instance starts; see
    // ChunkScanFrontier for the invariant the four threads share (PERF_TODO C3).
    private readonly ChunkScanFrontier _frontier = new(SortedOffsets);

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
    // from the shared ChunkScanFrontier below (PERF_TODO C3). Internal so the tests can build a
    // frontier over the real array.
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
    /// The closest chunk inside the view radius that is not loaded and not already claimed, or null
    /// if there is none. Internal so the tests can drive one scan without starting a thread.
    /// The centre and the removal version are both sampled once, up front, before any probe: a
    /// version bump that lands mid-scan is missed by this call and caught by the next, which is
    /// exactly what the pre-C3 full scan did with a chunk freed behind its read head.
    /// </summary>
    internal Vector3? FindClosestNeededChunk()
    {
        var camChunk = (camera.Position / Chunk.Size).Floor();
        return _frontier.FindNext(camChunk, chunkManager.ChunkRemovalVersion, _needsChunk);
    }
}

