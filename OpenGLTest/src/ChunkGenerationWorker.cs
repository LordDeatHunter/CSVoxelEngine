using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace silkgltriangle;

/// <summary>
/// Handles continuous chunk generation in a background task.
/// Generates chunks in concentric rings radiating from the player.
/// </summary>
public class ChunkGenerationWorker(ChunkManager chunkManager, Camera camera)
{
    private bool _isRunning;

    /// <summary>
    /// Starts the chunk generation worker in a background task.
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;
        
        _isRunning = true;
        Task.Run(GenerationLoop);
    }

    /// <summary>
    /// Stops the chunk generation worker.
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
    }

    private async Task GenerationLoop()
    {
        while (_isRunning)
        {
            try
            {
                var queuedCount = await GenerateChunksInRings();
                
                var delay = queuedCount > 0 
                    ? WorldGenerationConfig.GenerationDelayActiveMs 
                    : WorldGenerationConfig.GenerationDelayIdleMs;
                    
                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in chunk generation: {ex}");
                await Task.Delay(1000);
            }
        }
    }

    private async Task<int> GenerateChunksInRings()
    {
        var camChunk = (camera.Position / Chunk.Size).Floor();
        var queuedCount = 0;

        for (var ring = 0; ring <= WorldGenerationConfig.ChunkRadius && 
             queuedCount < WorldGenerationConfig.MaxChunksQueuedPerCycle; ring++)
        {
            var candidatesInRing = GetCandidatesInRing(camChunk, ring);
            
            foreach (var pos in candidatesInRing)
            {
                if (queuedCount >= WorldGenerationConfig.MaxChunksQueuedPerCycle)
                    break;
                
                chunkManager.QueueChunk(pos);
                queuedCount++;

                if (queuedCount % WorldGenerationConfig.YieldEveryNChunks == 0)
                    await Task.Delay(WorldGenerationConfig.YieldDelayMs);
            }

            if (candidatesInRing.Count > 0 && ring < WorldGenerationConfig.CloseRingThreshold)
                await Task.Delay(WorldGenerationConfig.CloseRingDelayMs);
        }

        return queuedCount;
    }

    private List<Vector3> GetCandidatesInRing(Vector3 center, int ring)
    {
        var candidates = new List<Vector3>();
        var ringDistSqMin = ring * ring;
        const int maxDistSq = WorldGenerationConfig.ChunkRadiusSq;

        for (int dx = -ring; dx <= ring; dx++)
        for (int dy = -ring; dy <= ring; dy++)
        for (int dz = -ring; dz <= ring; dz++)
        {
            var distSq = dx * dx + dy * dy + dz * dz;
            
            if (distSq < ringDistSqMin || distSq > maxDistSq)
                continue;

            var pos = center + new Vector3(dx, dy, dz);
            if (chunkManager.NeedsChunk(pos))
            {
                candidates.Add(pos);
            }
        }

        return candidates;
    }
}

