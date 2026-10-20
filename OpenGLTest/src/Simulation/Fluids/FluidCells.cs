using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Lod;

namespace VoxelEngine.Simulation.Fluids;

/// <summary>The fluid tick's cell access for one frame: reads and writes through a direct-mapped chunk cache and collects the chunks the writes touch for one MarkDirty batch. Owned by <see cref="FluidManager"/>; main thread only.</summary>
internal sealed class FluidCells
{
    // Chunks touched by this frame's tick slice, remeshed once the slice is done. Packed chunk
    // keys rather than Vector3 so the set never pays the float-vector hashing tax.
    private readonly HashSet<long> _dirtyChunks = [];

    // The manager the current tick reads and writes through; set by Update before the ticks run
    private ChunkManager _chunks = null!;

    // Where the tick reports a block write (lava crusting to cobblestone, dirt turning to mud)
    // that changes the far LOD's surface; set by Update, null in the headless tests, which
    // records nothing
    private LodManager? _lod;

    // Direct-mapped chunk-lookup cache for one tick. Every simulation cell read goes through
    // TryGetCell; without this each read is a Vector3 hash lookup in the chunk dictionary. A
    // slope search spans at most a handful of chunks, so 16 slots hold every key it can reach,
    // validated by the packed chunk key (always non-negative, so -1 is a safe empty sentinel).
    // The keys are cleared at the top of every Tick: an unload can replace a chunk between
    // frames, and a cached reference must never outlive that. Only the Chunk is cached, never
    // its block/state arrays — SetBlockRaw swaps in a fresh state array on the first non-zero
    // write, so a cached array pointer would miss later fluid state.
    private const int ChunkCacheSize = 16;
    private const int ChunkCacheMask = ChunkCacheSize - 1;
    private readonly Chunk?[] _chunkCache = new Chunk?[ChunkCacheSize];
    private readonly long[] _chunkCacheKeys = new long[ChunkCacheSize];

    /// <summary>The manager this frame's tick reads and writes through.</summary>
    internal ChunkManager Chunks => _chunks;

    /// <summary>Binds this frame's managers and empties the chunk cache; called by <see cref="FluidManager.Update"/> before any tick runs.</summary>
    internal void BeginFrame(ChunkManager chunks, LodManager? lod)
    {
        // The tick's helpers read through this and its chunk cache, so they need no manager
        // argument of their own
        _chunks = chunks;
        _lod = lod;

        // The chunk cache is valid for one frame only: an unload can replace a chunk between
        // frames, and a reference cached before it must never be handed back
        _chunkCacheKeys.AsSpan().Fill(-1L);
    }

    /// <summary>Marks every chunk this frame's writes touched dirty, once each, then forgets them.</summary>
    internal void FlushDirty(ChunkManager chunks)
    {
        if (_dirtyChunks.Count > 0)
        {
            foreach (var key in _dirtyChunks)
            {
                FluidCellKey.Unpack(key, out var cx, out var cy, out var cz);
                chunks.MarkDirty(new Vector3(cx, cy, cz));
            }
            _dirtyChunks.Clear();
        }
    }

    /// <summary>
    /// The loaded chunk owning a cell, through the tick's direct-mapped cache. A miss probes
    /// the chunk dictionary once and stores the reference; because the keys are cleared every
    /// tick, a hit can never hand back a chunk an unload has since replaced. Only the
    /// <see cref="Chunk"/> is cached, never its block or state arrays: <see cref="Chunk.SetBlockRaw"/>
    /// swaps in a fresh state array on the first non-zero write, so a cached array would miss
    /// later state. The miss path goes through <see cref="ChunkManager.ChunkAt"/>, which keeps
    /// the tests' contract that a chunk without ready block data reads as unloaded.
    /// </summary>
    private bool TryGetChunk(int x, int y, int z, out Chunk? chunk)
    {
        var key = FluidCellKey.Pack(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);
        var slot = (int)((key ^ (key >> 32)) & ChunkCacheMask);

        if (_chunkCacheKeys[slot] != key)
        {
            _chunkCacheKeys[slot] = key;
            chunk = _chunks.ChunkAt(x, y, z);
            _chunkCache[slot] = chunk;
        }
        else
        {
            chunk = _chunkCache[slot];
        }

        return chunk != null;
    }

    /// <summary>
    /// Reads a world cell's block id and state through the tick's chunk cache. The contract
    /// matches <see cref="ChunkManager.TryGetCell"/>: false means the chunk is not loaded, and
    /// an unloaded cell must never read as air.
    /// </summary>
    internal bool TryGetCell(int x, int y, int z, out byte id, out byte state)
    {
        if (!TryGetChunk(x, y, z, out var chunk))
        {
            id = 0;
            state = 0;
            return false;
        }

        var lx = x & Chunk.SizeMask;
        var ly = y & Chunk.SizeMask;
        var lz = z & Chunk.SizeMask;
        id = chunk!.GetLocalBlock(lx, ly, lz);
        state = chunk.GetLocalState(lx, ly, lz);
        return true;
    }

    /// <summary>
    /// Writes a cell's id and state with no remesh of its own, queueing the chunks whose mesh
    /// the change shows up in for the batch at the end of the tick. Fluids pass light and cast
    /// no ambient occlusion, so only the cell's own chunk and the chunks across a border it
    /// touches are affected — never the whole light radius.
    /// </summary>
    internal void Write(int x, int y, int z, byte id, byte state)
    {
        if (!TryGetChunk(x, y, z, out var chunk)) return;

        chunk!.SetBlockRaw(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask, id, state);
        FluidCellKey.AddTouchedChunks(x, y, z, _dirtyChunks);

        // A block write — lava crusting to cobblestone, dirt turning to mud — leaves a real block
        // behind, which can raise or re-tile the far LOD like a placement. Fluid and air writes
        // never count as its surface, so they are skipped rather than read as a sub-surface
        // removal. A write at or below the column's generated water level (mud forming on a lake
        // or sea bed, lava crusting under the sea) never moves the far LOD either, which keeps
        // drawing the generated depth band there; only a block left above the water line can
        // raise or re-tile it.
        if (_lod != null && id != 0 && !BlockType.IsFluid(id) && AboveGeneratedWater(x, y, z)
            && LodSurface.RecordEdit(_chunks, x, y, z))
            _lod.MarkEditedColumn(x, z);
    }

    /// <summary>Whether world cell (x, y, z) sits above the column's water level from the world
    /// source (<see cref="ChunkManager.WorldSource"/>: worldgen's generated level by default).</summary>
    private bool AboveGeneratedWater(int x, int y, int z) =>
        y > _chunks.WorldSource.GetColumn(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            .WaterLevel[VoxelCoords.ColumnIndex(x & Chunk.SizeMask, z & Chunk.SizeMask)];
}
