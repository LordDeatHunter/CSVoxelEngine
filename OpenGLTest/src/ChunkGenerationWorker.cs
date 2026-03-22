using System;
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
                Console.WriteLine($"Error in chunk generation: {ex}");
                Thread.Sleep(1000);
            }
        }
    }

    private Vector3? FindClosestNeededChunk()
    {
        var camChunk = (camera.Position / Chunk.Size).Floor();
        const int radius = WorldGenerationConfig.ChunkRadius;
        const int maxDistSq = WorldGenerationConfig.ChunkRadiusSq;

        Vector3? bestPos = null;
        var bestDistSq = int.MaxValue;

        for (int dx = -radius; dx <= radius; dx++)
        for (int dy = -radius; dy <= radius; dy++)
        for (int dz = -radius; dz <= radius; dz++)
        {
            var distSq = dx * dx + dy * dy + dz * dz;
            if (distSq > maxDistSq || distSq >= bestDistSq)
                continue;

            var pos = camChunk + new Vector3(dx, dy, dz);
            if (!chunkManager.NeedsChunk(pos)) continue;
            bestPos = pos;
            bestDistSq = distSq;
        }

        return bestPos;
    }
}

