using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
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
    private readonly ConcurrentDictionary<Vector3, byte> _generatingChunks = new();


    public void Update(Camera camera)
    {
        if (camera == null) return;

        var camChunk = (camera.Position / Chunk.Size).Floor();

        UnloadDistantChunks(camChunk);
        CleanupStaleGenerations(camChunk);
        ApplyCompletedMeshes(camChunk);
        CleanupDistantDirtyChunks(camChunk);
        ProcessDirtyChunks(camChunk);
    }

    private void UnloadDistantChunks(Vector3 camChunk)
    {
        var chunksToRemove = Chunks.Keys
            .Where(pos => Vector3.DistanceSquared(pos, camChunk) > WorldGenerationConfig.ChunkRadiusSq)
            .ToList();

        foreach (var pos in chunksToRemove)
        {
            if (!Chunks.TryRemove(pos, out var chunk)) continue;
            
            chunk.Dispose();
            _queuedChunkPositions.TryRemove(pos, out _);
            _generatingChunks.TryRemove(pos, out _);

            foreach (var offset in Vector3Directions.AllDirections)
            {
                var neighborPos = pos + offset;
                if (Chunks.ContainsKey(neighborPos))
                    _dirtyChunks.TryAdd(neighborPos, 0);
            }
        }
    }

    private void CleanupStaleGenerations(Vector3 camChunk)
    {
        var staleGenerations = _generatingChunks.Keys
            .Where(pos => Vector3.DistanceSquared(pos, camChunk) > WorldGenerationConfig.ChunkRadiusSq)
            .ToList();
        
        foreach (var pos in staleGenerations)
        {
            _generatingChunks.TryRemove(pos, out _);
        }
    }

    private void ApplyCompletedMeshes(Vector3 camChunk)
    {
        var appliedCount = 0;
        
        while (appliedCount < WorldGenerationConfig.MaxChunksPerFrame && 
               _completedMeshQueue.TryDequeue(out var item))
        {
            var (pos, chunk, meshData) = item;
            
            if (Vector3.DistanceSquared(pos, camChunk) > WorldGenerationConfig.ChunkRadiusSq)
            {
                _generatingChunks.TryRemove(pos, out _);
                continue;
            }
            
            chunk.UpdateMesh(meshData);
            _generatingChunks.TryRemove(pos, out _);

            foreach (var offset in Vector3Directions.AllDirections)
            {
                var neighborPos = pos + offset;
                if (Chunks.ContainsKey(neighborPos))
                    _dirtyChunks.TryAdd(neighborPos, 0);
            }

            appliedCount++;
        }
    }

    private void CleanupDistantDirtyChunks(Vector3 camChunk)
    {
        var distantDirtyChunks = _dirtyChunks.Keys
            .Where(pos => Vector3.DistanceSquared(pos, camChunk) > WorldGenerationConfig.ChunkRadiusSq)
            .ToList();
        
        foreach (var pos in distantDirtyChunks)
        {
            _dirtyChunks.TryRemove(pos, out _);
        }
    }

    private void ProcessDirtyChunks(Vector3 camChunk)
    {
        var sortedDirtyChunks = _dirtyChunks.Keys
            .Select(pos => new { pos, distSq = Vector3.DistanceSquared(pos, camChunk) })
            .OrderBy(x => x.distSq)
            .Select(x => x.pos)
            .ToList();

        var processedCount = 0;

        foreach (var pos in sortedDirtyChunks)
        {
            if (processedCount >= WorldGenerationConfig.MaxDirtyChunksPerFrame)
                break;

            if (_generatingChunks.Count >= WorldGenerationConfig.MaxConcurrentMeshGenerations)
                break;

            if (!_dirtyChunks.TryRemove(pos, out _))
                continue;

            if (Vector3.DistanceSquared(pos, camChunk) > WorldGenerationConfig.ChunkRadiusSq)
                continue;

            if (!Chunks.TryGetValue(pos, out var chunk))
                continue;

            if (HasAllNeighbors(pos) && !_generatingChunks.ContainsKey(pos))
            {
                StartMeshGeneration(pos, chunk);
            }
            else if (!HasAllNeighbors(pos))
            {
                _dirtyChunks.TryAdd(pos, 0);
            }

            processedCount++;
        }
    }

    private void StartMeshGeneration(Vector3 pos, Chunk chunk)
    {
        _generatingChunks.TryAdd(pos, 0);
        
        Task.Run(() =>
        {
            try
            {
                var meshData = chunk.GenerateMeshData(this);
                _completedMeshQueue.Enqueue((pos, chunk, meshData));
            }
            finally
            {
                _generatingChunks.TryRemove(pos, out _);
            }
        });
    }


    /// <summary>
    /// Queues a new chunk for creation and marks it for mesh generation.
    /// </summary>
    public void QueueChunk(Vector3 pos)
    {
        if (Chunks.ContainsKey(pos) || !_queuedChunkPositions.TryAdd(pos, 0))
            return;

        var chunk = new Chunk(gl, pos);
        Chunks[pos] = chunk;

        _dirtyChunks.TryAdd(pos, 0);
        
        foreach (var offset in Vector3Directions.AllDirections)
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
        if (!HasAllNeighbors(pos)) return;
        
        var meshData = chunk.GenerateMeshData(this);
        chunk.UpdateMesh(meshData);
    }

    /// <summary>
    /// Checks if a chunk needs to be created at the given position.
    /// </summary>
    public bool NeedsChunk(Vector3 pos)
    {
        return !Chunks.ContainsKey(pos) && !_queuedChunkPositions.ContainsKey(pos);
    }
    
    /// <summary>
    /// Checks if all 6 cardinal neighbors of a chunk are loaded.
    /// </summary>
    private bool HasAllNeighbors(Vector3 pos)
    {
        return Vector3Directions.AllDirections.All(offset => Chunks.ContainsKey(pos + offset));
    }
}
