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
    
    // The sequence number stamps each result with the order it finished in. ApplyCompletedMeshes
    // sorts by distance, and two results for the same chunk (an older one re-queued by the
    // upload budget, plus a newer rebuild) tie there, so without it the unstable sort could
    // apply them newest-first and leave the stale mesh on screen.
    private readonly ConcurrentQueue<(Vector3 pos, Chunk chunk, ChunkMeshData meshData, long seq)> _completedMeshQueue = new();
    private long _meshSequence;
    private readonly ConcurrentDictionary<Vector3, byte> _dirtyChunks = new();
    private readonly ConcurrentDictionary<Vector3, byte> _queuedChunkPositions = new();

    // Chunks whose block data was just generated, handed to the main thread so the fluid tick
    // can wake the water they brought in (FluidManager.WakeLoadedChunks). Filled by the
    // generation workers, drained on the main thread; entries can outlive an unload, so the
    // consumer re-checks that the chunk is still the live one at its position.
    private readonly ConcurrentQueue<Chunk> _loadedChunks = new();

    // Track active mesh generation to avoid duplicates
    private readonly ConcurrentDictionary<Vector3, byte> _generatingMeshes = new();
    private readonly SemaphoreSlim _meshSemaphore = new(WorldGenerationConfig.MaxConcurrentMeshGenerations);

    // World cells the player built (integer coords): breaking one never fells a tree, it just
    // drops as-is with no toppling entity. Main thread only, like every other block edit.
    // Unloaded chunks regenerate from worldgen (edits don't persist), so their entries are
    // purged on unload to keep the set — and the felling decision — truthful.
    private readonly HashSet<(int X, int Y, int Z)> _playerPlaced = new();

    /// <summary>Whether the world cell was built by the player rather than generated.</summary>
    public bool IsPlayerPlaced(int x, int y, int z) => _playerPlaced.Contains((x, y, z));

    /// <summary>Records a player placement (called by <see cref="Chunk.PlaceBlockAt"/>).</summary>
    public void MarkPlayerPlaced(int x, int y, int z) => _playerPlaced.Add((x, y, z));

    /// <summary>Forgets a placement once its block is gone (mined, felled or blasted).</summary>
    public void UnmarkPlayerPlaced(int x, int y, int z) => _playerPlaced.Remove((x, y, z));

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

            // Edits don't survive an unload (the chunk regenerates from worldgen), so
            // placements inside it stop being player-placed too
            var x0 = (int)pos.X * Chunk.Size;
            var y0 = (int)pos.Y * Chunk.Size;
            var z0 = (int)pos.Z * Chunk.Size;
            _playerPlaced.RemoveWhere(cell =>
                cell.X >= x0 && cell.X < x0 + Chunk.Size &&
                cell.Y >= y0 && cell.Y < y0 + Chunk.Size &&
                cell.Z >= z0 && cell.Z < z0 + Chunk.Size);
        }
    }

    private void ApplyCompletedMeshes(Vector3 camChunk)
    {
        // Drain into a temporary list to sort by distance
        var pending = new List<(Vector3 pos, Chunk chunk, ChunkMeshData meshData, long seq)>();
        while (_completedMeshQueue.TryDequeue(out var item))
        {
            pending.Add(item);
        }

        if (pending.Count == 0) return;

        // Sort closest first, then oldest first. List.Sort is unstable, so the sequence
        // tiebreak is what guarantees a newer mesh for a chunk is applied after an older one
        // rather than being overwritten by it.
        pending.Sort((a, b) =>
        {
            var byDistance = Vector3.DistanceSquared(a.pos, camChunk)
                .CompareTo(Vector3.DistanceSquared(b.pos, camChunk));
            return byDistance != 0 ? byDistance : a.seq.CompareTo(b.seq);
        });

        var appliedCount = 0;

        foreach (var (pos, chunk, meshData, seq) in pending)
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
                // Re-queue for next frame, keeping the original sequence so a rebuild that
                // finishes in the meantime still sorts after it
                _completedMeshQueue.Enqueue((pos, chunk, meshData, seq));
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
                if (meshData == null)
                {
                    // A face neighbour was unloaded between the scheduling check and this
                    // snapshot; meshing across that hole would bake border artifacts, so the
                    // build is dropped and the chunk retried once the world settles
                    _dirtyChunks.TryAdd(pos, 0);
                    return;
                }

                _completedMeshQueue.Enqueue((pos, chunk, meshData, Interlocked.Increment(ref _meshSequence)));
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
        _loadedChunks.Enqueue(chunk);

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
    /// Pops one chunk that finished generating since the last call, oldest first. The caller
    /// must confirm it is still the chunk registered at its position, because an unload can
    /// happen between the enqueue and the drain.
    /// </summary>
    public bool TryDequeueLoaded(out Chunk chunk) => _loadedChunks.TryDequeue(out chunk);

    /// <summary>
    /// Marks a chunk as dirty (needs remeshing).
    /// </summary>
    public void MarkDirty(Vector3 pos)
    {
        _dirtyChunks.TryAdd(pos, 0);
    }

    /// <summary>
    /// Marks every loaded chunk within <paramref name="radiusBlocks"/> of the world-space
    /// box [<paramref name="min"/>, <paramref name="max"/>] dirty. Voxel light travels up
    /// to <see cref="WorldGenerationConfig.LightPropagationRadius"/> blocks from an edit,
    /// so all of these chunks need their light recomputed — not just face-adjacent ones.
    /// Async and budgeted like any other dirty chunk; the edited chunk itself is usually
    /// remeshed synchronously by the caller already, so it is skipped here.
    /// </summary>
    public void MarkAreaDirty(Vector3 min, Vector3 max, int radiusBlocks, Vector3? skip = null)
    {
        var x0 = (int)MathF.Floor((min.X - radiusBlocks) / Chunk.Size);
        var y0 = (int)MathF.Floor((min.Y - radiusBlocks) / Chunk.Size);
        var z0 = (int)MathF.Floor((min.Z - radiusBlocks) / Chunk.Size);
        var x1 = (int)MathF.Floor((max.X + radiusBlocks) / Chunk.Size);
        var y1 = (int)MathF.Floor((max.Y + radiusBlocks) / Chunk.Size);
        var z1 = (int)MathF.Floor((max.Z + radiusBlocks) / Chunk.Size);

        for (var x = x0; x <= x1; x++)
        for (var y = y0; y <= y1; y++)
        for (var z = z0; z <= z1; z++)
        {
            var pos = new Vector3(x, y, z);
            if (skip.HasValue && pos == skip.Value) continue;
            if (Chunks.ContainsKey(pos))
                _dirtyChunks.TryAdd(pos, 0);
        }
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
        if (meshData == null)
        {
            // Lost a face neighbour to an unload mid-call: retry rather than bake a hole in
            _dirtyChunks.TryAdd(pos, 0);
            return;
        }

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
    /// The loaded chunk owning a world cell, or null when it is not loaded. A chunk present in
    /// <see cref="Chunks"/> always has ready block data (<see cref="QueueChunk"/> generates
    /// before inserting), but the flag is checked anyway so the contract holds if that changes.
    /// </summary>
    public Chunk? ChunkAt(int x, int y, int z)
    {
        var chunkPos = new Vector3(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);
        return Chunks.TryGetValue(chunkPos, out var chunk) && chunk.IsBlockDataReady ? chunk : null;
    }

    /// <summary>
    /// Reads a world cell's block id and state byte, returning false when its chunk is not
    /// loaded. The fluid simulation must use this rather than <see cref="GetBlockGlobal"/>:
    /// "unloaded" reads as air there, which would make water pour into unloaded space.
    /// </summary>
    public bool TryGetCell(int x, int y, int z, out byte id, out byte state)
    {
        var chunk = ChunkAt(x, y, z);
        if (chunk == null)
        {
            id = 0;
            state = 0;
            return false;
        }

        var lx = x & Chunk.SizeMask;
        var ly = y & Chunk.SizeMask;
        var lz = z & Chunk.SizeMask;
        id = chunk.GetLocalBlock(lx, ly, lz);
        state = chunk.GetLocalState(lx, ly, lz);
        return true;
    }

    /// <summary>Reads a world cell's state byte; 0 when its chunk is not loaded (which is also
    /// what a source reads as, so pair it with a loaded check when that matters).</summary>
    public byte GetStateGlobal(int x, int y, int z)
    {
        var chunk = ChunkAt(x, y, z);
        return chunk?.GetLocalState(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask) ?? 0;
    }

    /// <summary>
    /// World Y of the fluid surface in the cell (x, y, z): <c>y + 1</c> when another fluid cell
    /// sits directly above (stacked water has no seam), otherwise <c>y</c> plus the cell's own
    /// <see cref="FluidState.SurfaceHeight"/>. Returns <see cref="float.NegativeInfinity"/>
    /// when the cell holds no fluid, so callers can compare against it unconditionally.
    /// </summary>
    public float GetFluidSurfaceGlobal(int x, int y, int z)
    {
        if (!TryGetCell(x, y, z, out var id, out var state) || !BlockType.IsFluid(id))
            return float.NegativeInfinity;

        if (TryGetCell(x, y + 1, z, out var above, out _) && BlockType.IsFluid(above))
            return y + 1f;

        return y + FluidState.SurfaceHeight(state);
    }

    // The four horizontal neighbours a flow can be pulled toward, as (dx, dz)
    private static readonly (int X, int Z)[] FlowNeighbours = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>
    /// Unit-length horizontal direction the fluid in the cell (x, y, z) flows, or
    /// <see cref="Vector3.Zero"/> when the cell holds no fluid or the fluid is still. Mirrors
    /// Minecraft's <c>FlowingFluid.getFlow</c>: every horizontal neighbour pulls the flow toward
    /// itself in proportion to how much lower its own surface sits, a replaceable cell the fluid
    /// can drop off into counting as a whole source lower still, and a solid neighbour
    /// contributing nothing. A pool of sources therefore cancels out to zero, while a skirt
    /// points away from its source and toward the edge it pours over.
    /// </summary>
    public Vector3 GetFluidFlowGlobal(int x, int y, int z)
    {
        if (!TryGetCell(x, y, z, out var id, out var state) || !BlockType.IsFluid(id))
            return Vector3.Zero;

        // The cell's own level, deliberately not GetFluidSurfaceGlobal: pinning a cell with
        // fluid above it to a full block would flatten a falling column's gradient to nothing
        var ownHeight = FluidState.SurfaceHeight(state);
        var flow = Vector3.Zero;

        foreach (var (dx, dz) in FlowNeighbours)
        {
            if (!TryGetCell(x + dx, y, z + dz, out var neighbourId, out var neighbourState))
                continue;

            float neighbourHeight;
            if (neighbourId == id)
            {
                neighbourHeight = FluidState.SurfaceHeight(neighbourState);
            }
            else if (BlockType.IsReplaceable(neighbourId))
            {
                // Air or a fragile block: the fluid only gets pulled that way if it would drop
                // off there, and a drop counts as a full source lower than any level could be
                if (TryGetCell(x + dx, y - 1, z + dz, out var belowId, out var belowState) && belowId == id)
                    neighbourHeight = FluidState.SurfaceHeight(belowState) - FluidState.SourceHeight;
                else
                    continue;
            }
            else
            {
                continue; // a solid neighbour dams the flow instead of steering it
            }

            flow += new Vector3(dx, 0f, dz) * (ownHeight - neighbourHeight);
        }

        return flow.LengthSquared() > 1e-6f ? Vector3.Normalize(flow) : Vector3.Zero;
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
