using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Silk.NET.OpenGL;

namespace silkgltriangle;

public class ChunkManager
{
    public readonly ConcurrentDictionary<Vector3, Chunk> Chunks = new();
    private readonly ConcurrentQueue<(Vector3 pos, Chunk chunk, ChunkMeshData meshData)> chunkQueue = new();
    private readonly ConcurrentQueue<Vector3> dirtyChunks = new();
    private readonly ConcurrentDictionary<Vector3, byte> queuedChunkPositions = new();

    private readonly GL gl;
    private readonly int activeChunkRadius = 12;
    private readonly int activeChunkRadiusSq;
    private const int maxChunksPerFrame = 256;
    private const int maxDirtyPerFrame = 256;

    public ChunkManager(GL gl)
    {
        this.gl = gl;
        activeChunkRadiusSq = activeChunkRadius * activeChunkRadius;
    }

    public void Update(Camera camera)
    {
        if (camera == null)
            return;

        var camChunk = (camera.Position / Chunk.Size).Floor();
        var chunksToRemove = new List<Vector3>();

        foreach (var chunkPos in Chunks.Keys)
        {
            var offset = chunkPos - camChunk;
            var distSq = offset.LengthSquared();

            if (distSq > activeChunkRadiusSq)
            {
                chunksToRemove.Add(chunkPos);
            }
        }

        foreach (var pos in chunksToRemove)
        {
            if (!Chunks.TryGetValue(pos, out var chunk)) continue;
            chunk.Dispose();

            foreach (var offset in Vector3Directions.AllDirections)
            {
                var neighborPos = pos + offset;
                if (Chunks.ContainsKey(neighborPos))
                {
                    dirtyChunks.Enqueue(neighborPos);
                }
            }

            Chunks.TryRemove(pos, out _);
            queuedChunkPositions.TryRemove(pos, out _);
        }

        int chunkCount = 0;
        while (chunkCount < maxChunksPerFrame && chunkQueue.TryDequeue(out var item))
        {
            var (pos, chunk, meshData) = item;
            chunk.UpdateMesh(meshData);
            Chunks[pos] = chunk;

            foreach (var offset in Vector3Directions.AllDirections)
            {
                var neighborPos = pos + offset;
                if (Chunks.ContainsKey(neighborPos))
                {
                    dirtyChunks.Enqueue(neighborPos);
                }
            }

            chunkCount++;
        }

        var dirtyProcessed = 0;
        while (dirtyProcessed < maxDirtyPerFrame && dirtyChunks.TryDequeue(out var pos))
        {
            if (Chunks.TryGetValue(pos, out var chunk))
            {
                if (HasAllNeighbors(pos))
                {
                    var meshData = chunk.GenerateMeshData(this);
                    chunk.UpdateMesh(meshData);
                }
                else
                {
                    dirtyChunks.Enqueue(pos);
                }
            }
            dirtyProcessed++;
        }
    }

    public void QueueChunk(Vector3 pos)
    {
        if (Chunks.ContainsKey(pos) || !queuedChunkPositions.TryAdd(pos, 0))
            return;

        var chunk = new Chunk(gl, pos);
        Chunks[pos] = chunk;

        dirtyChunks.Enqueue(pos);
        foreach (var offset in Vector3Directions.AllDirections)
        {
            var neighborPos = pos + offset;
            if (Chunks.ContainsKey(neighborPos))
                dirtyChunks.Enqueue(neighborPos);
        }
    }

    public void MarkDirty(Vector3 pos)
    {
        dirtyChunks.Enqueue(pos);
    }

    public void ImmediateRemesh(Vector3 pos)
    {
        if (!Chunks.TryGetValue(pos, out var chunk) || !HasAllNeighbors(pos)) return;
        var meshData = chunk.GenerateMeshData(this);
        chunk.UpdateMesh(meshData);
    }

    public bool NeedsChunk(Vector3 pos)
    {
        return !Chunks.ContainsKey(pos) && !queuedChunkPositions.ContainsKey(pos);
    }
    
    private bool HasAllNeighbors(Vector3 pos) => Vector3Directions.AllDirections.All(offset => Chunks.ContainsKey(pos + offset));
}
