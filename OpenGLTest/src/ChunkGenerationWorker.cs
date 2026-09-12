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

    /// <summary>
    /// Starts the chunk generation workers.
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;

        var workerCount = WorldGenerationConfig.MaxConcurrentBlockDataGenerations;
        for (int i = 0; i < workerCount; i++)
        {
            var thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"ChunkGen-{i}",
                Priority = ThreadPriority.BelowNormal
            };
            thread.Start();
        }
    }

    /// <summary>
    /// Stops the chunk generation workers.
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
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
                    Thread.Sleep(WorldGenerationConfig.GenerationDelayIdleMs);
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine($"Error in chunk generation: {ex}");
                Thread.Sleep(1000);
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

