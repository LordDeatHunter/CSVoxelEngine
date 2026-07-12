using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Manages chunk loading, unloading, and mesh generation.
/// </summary>
public class ChunkManager(GL gl)
{
    public readonly ConcurrentDictionary<Vector3, Chunk> Chunks = new();
    
    private readonly ConcurrentQueue<(Vector3 pos, Chunk chunk, ChunkMeshData meshData)> _completedMeshQueue = new();
    private readonly ConcurrentDictionary<Vector3, byte> _dirtyChunks = new();
    private readonly ConcurrentDictionary<Vector3, byte> _queuedChunkPositions = new();

    // Track active mesh generation to avoid duplicates
    private readonly ConcurrentDictionary<Vector3, byte> _generatingMeshes = new();
    private readonly SemaphoreSlim _meshSemaphore = new(WorldGenerationConfig.MaxConcurrentMeshGenerations);

    public void Update(Camera camera)
    {
        if (camera == null) return;

        var camChunk = (camera.Position / Chunk.Size).Floor();

        UnloadDistantChunks(camChunk);
        ApplyCompletedMeshes(camChunk);
        ProcessDirtyChunks(camChunk);
    }

    private void UnloadDistantChunks(Vector3 camChunk)
    {
        foreach (var pos in Chunks.Keys)
        {
            if (Vector3.DistanceSquared(pos, camChunk) <= WorldGenerationConfig.ChunkRadiusSq)
                continue;
                
            if (!Chunks.TryRemove(pos, out var chunk)) continue;
            
            chunk.Dispose();
            _queuedChunkPositions.TryRemove(pos, out _);
            _generatingMeshes.TryRemove(pos, out _);
            _dirtyChunks.TryRemove(pos, out _);
        }
    }

    private void ApplyCompletedMeshes(Vector3 camChunk)
    {
        // Drain into a temporary list to sort by distance
        var pending = new List<(Vector3 pos, Chunk chunk, ChunkMeshData meshData)>();
        while (_completedMeshQueue.TryDequeue(out var item))
        {
            pending.Add(item);
        }
        
        if (pending.Count == 0) return;
        
        // Sort closest first
        pending.Sort((a, b) =>
            Vector3.DistanceSquared(a.pos, camChunk)
                .CompareTo(Vector3.DistanceSquared(b.pos, camChunk)));

        var appliedCount = 0;
        
        foreach (var (pos, chunk, meshData) in pending)
        {
            // Skip chunks that moved out of range or were unloaded
            if (Vector3.DistanceSquared(pos, camChunk) > WorldGenerationConfig.ChunkRadiusSq ||
                !Chunks.ContainsKey(pos))
                continue;
            
            if (appliedCount < WorldGenerationConfig.MaxMeshUploadsPerFrame)
            {
                chunk.UpdateMesh(meshData);
                appliedCount++;
            }
            else
            {
                // Re-queue for next frame
                _completedMeshQueue.Enqueue((pos, chunk, meshData));
            }
        }
    }

    private void ProcessDirtyChunks(Vector3 camChunk)
    {
        if (_dirtyChunks.IsEmpty) return;

        // Snapshot and sort dirty chunks by distance
        var dirtyPositions = _dirtyChunks.Keys.ToArray();
        
        Array.Sort(dirtyPositions, (a, b) =>
            Vector3.DistanceSquared(a, camChunk)
                .CompareTo(Vector3.DistanceSquared(b, camChunk)));

        var processedCount = 0;

        foreach (var pos in dirtyPositions)
        {
            if (processedCount >= WorldGenerationConfig.MaxDirtyChunksPerFrame)
                break;

            // Out of range - just remove
            if (Vector3.DistanceSquared(pos, camChunk) > WorldGenerationConfig.ChunkRadiusSq)
            {
                _dirtyChunks.TryRemove(pos, out _);
                continue;
            }

            if (!Chunks.TryGetValue(pos, out var chunk))
            {
                _dirtyChunks.TryRemove(pos, out _);
                continue;
            }

            // Not ready yet or already meshing - skip, will retry next frame
            if (!chunk.IsBlockDataReady || _generatingMeshes.ContainsKey(pos))
                continue;

            if (!HasAllNeighbors(pos))
                continue;

            // Ready to mesh
            if (!_dirtyChunks.TryRemove(pos, out _))
                continue;

            StartMeshGeneration(pos, chunk);
            processedCount++;
        }
    }

    private void StartMeshGeneration(Vector3 pos, Chunk chunk)
    {
        if (!_generatingMeshes.TryAdd(pos, 0))
            return;
        
        Task.Run(async () =>
        {
            await _meshSemaphore.WaitAsync();
            try
            {
                if (!Chunks.ContainsKey(pos))
                    return;
                    
                var meshData = chunk.GenerateMeshData(this);
                _completedMeshQueue.Enqueue((pos, chunk, meshData));
            }
            finally
            {
                _generatingMeshes.TryRemove(pos, out _);
                _meshSemaphore.Release();
            }
        });
    }

    /// <summary>
    /// Creates a chunk and generates its block data synchronously.
    /// Called from the generation worker thread which already iterates by distance.
    /// </summary>
    public void QueueChunk(Vector3 pos)
    {
        if (Chunks.ContainsKey(pos) || !_queuedChunkPositions.TryAdd(pos, 0))
            return;

        // Create chunk AND generate block data right here.
        // This is called from the generation worker's background thread,
        // which already processes closest chunks first.
        var chunk = new Chunk(gl, pos, generateBlockData: true);
        Chunks[pos] = chunk;

        // Mark this chunk and neighbors dirty for meshing
        _dirtyChunks.TryAdd(pos, 0);
        
        foreach (var offset in Vector3Directions.CardinalDirections)
        {
            var neighborPos = pos + offset;
            if (Chunks.ContainsKey(neighborPos))
                _dirtyChunks.TryAdd(neighborPos, 0);
        }
    }

    /// <summary>
    /// Marks a chunk as dirty (needs remeshing).
    /// </summary>
    public void MarkDirty(Vector3 pos)
    {
        _dirtyChunks.TryAdd(pos, 0);
    }

    /// <summary>
    /// Immediately regenerates the mesh for a chunk (used for block breaking).
    /// </summary>
    public void ImmediateRemesh(Vector3 pos)
    {
        if (!Chunks.TryGetValue(pos, out var chunk)) return;
        if (!chunk.IsBlockDataReady) return;
        if (!HasAllNeighbors(pos)) return;
        
        var meshData = chunk.GenerateMeshData(this);
        chunk.UpdateMesh(meshData);
    }

    /// <summary>
    /// Reads a block at world coordinates. Unloaded chunks read as air.
    /// </summary>
    public byte GetBlockGlobal(int x, int y, int z)
    {
        var chunkPos = new Vector3(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);
        return Chunks.TryGetValue(chunkPos, out var chunk)
            ? chunk.GetLocalBlock(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask)
            : (byte)0;
    }

    /// <summary>
    /// Checks if a chunk needs to be created at the given position.
    /// </summary>
    public bool NeedsChunk(Vector3 pos)
    {
        return !Chunks.ContainsKey(pos) && !_queuedChunkPositions.ContainsKey(pos);
    }
    
    /// <summary>
    /// Checks if all 6 cardinal neighbors of a chunk are loaded and have block data ready.
    /// </summary>
    private bool HasAllNeighbors(Vector3 pos)
    {
        foreach (var offset in Vector3Directions.CardinalDirections)
        {
            if (!Chunks.TryGetValue(pos + offset, out var neighbor) || !neighbor.IsBlockDataReady)
                return false;
        }
        return true;
    }
}
