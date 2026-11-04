using System;
using System.Collections.Generic;

namespace VoxelEngine.Chunks;

/// <summary>A simulation's cell access for one frame: reads and writes through a direct-mapped chunk cache and collects the chunks the writes touch for one MarkDirty batch. Each simulation owns its own instance (the fluid tick through <c>FluidCells</c>, the leaf decay directly), cleared by its own <see cref="BeginFrame"/> and flushed by its own <see cref="FlushDirty"/>, so neither ever sees the other's cache or dirty set. Main thread only.</summary>
internal sealed class ChunkCellCache
{
    // Chunks touched by this frame's writes, remeshed by FlushDirty. Packed chunk keys (ChunkKey)
    // rather than Vector3 so the set never pays the float-vector hashing tax.
    private readonly HashSet<long> _dirtyChunks = [];

    // The manager this frame's reads and writes go through; set by BeginFrame before the ticks run
    private ChunkManager _chunks = null!;

    // Direct-mapped chunk-lookup cache for one frame. Every simulation cell read goes through
    // TryGetCell; without this each read is a Vector3 hash lookup in the chunk dictionary. A
    // slope search spans at most a handful of chunks, so 16 slots hold every key it can reach,
    // validated by the packed chunk key (always non-negative, so -1 is a safe empty sentinel).
    // The keys are cleared by every BeginFrame: an unload can replace a chunk between
    // frames, and a cached reference must never outlive that. Only the Chunk is cached, never
    // its block/state arrays — SetBlockRaw swaps in a fresh state array on the first non-zero
    // write, so a cached array pointer would miss later fluid state.
    private const int ChunkCacheSize = 16;
    private const int ChunkCacheMask = ChunkCacheSize - 1;
    private readonly Chunk?[] _chunkCache = new Chunk?[ChunkCacheSize];
    private readonly long[] _chunkCacheKeys = new long[ChunkCacheSize];

    /// <summary>The manager this frame's reads and writes go through.</summary>
    internal ChunkManager Chunks => _chunks;

    /// <summary>Binds the frame's chunk manager and empties the chunk cache; called by the owner before any tick runs.</summary>
    internal void BeginFrame(ChunkManager chunks)
    {
        _chunks = chunks;

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
                chunks.MarkDirty(ChunkKey.ToVector3(key));
            }
            _dirtyChunks.Clear();
        }
    }

    /// <summary>
    /// The loaded chunk owning a cell, through the frame's direct-mapped cache. A miss probes
    /// the chunk dictionary once and stores the reference; because the keys are cleared every
    /// frame, a hit can never hand back a chunk an unload has since replaced. Only the
    /// <see cref="Chunk"/> is cached, never its block or state arrays: <see cref="Chunk.SetBlockRaw"/>
    /// swaps in a fresh state array on the first non-zero write, so a cached array would miss
    /// later state. The miss path goes through <see cref="ChunkManager.ChunkAt"/>, which keeps
    /// the tests' contract that a chunk without ready block data reads as unloaded.
    /// </summary>
    private bool TryGetChunk(int x, int y, int z, out Chunk? chunk)
    {
        var key = ChunkKey.PackBlock(x, y, z);
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
    /// Reads a world cell's block id and state through the frame's chunk cache. The contract
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
    /// Writes a cell's id and state through the frame's chunk cache with no remesh of its own; false
    /// means the chunk is not loaded and nothing was written. With <paramref name="remesh"/> true it
    /// queues the chunks whose mesh the change shows up in for the <see cref="FlushDirty"/> batch:
    /// only the cell's own chunk and the chunks across a border it touches, never the light radius,
    /// which is right for fluids, leaves and tree wood, all of which pass light and cast no ambient
    /// occlusion. A leaf decay hydration rewrite passes false: the mesher never reads <c>LeafState</c>.
    /// </summary>
    internal bool Write(int x, int y, int z, byte id, byte state, bool remesh)
    {
        if (!TryGetChunk(x, y, z, out var chunk)) return false;

        chunk!.SetBlockRaw(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask, id, state);
        if (remesh) AddTouchedChunks(x, y, z, _dirtyChunks);
        return true;
    }

    /// <summary>Adds the chunk owning a cell, plus the face-adjacent chunk on every axis where
    /// the cell sits on a chunk border, to <paramref name="into"/> as packed chunk keys. A cell on a
    /// Y border and an X or Z border one also adds the edge-diagonal chunk across both: a beveled
    /// tree-wood shell's cap reads the vertical neighbor's horizontal neighbors, so an edit there
    /// changes the mesh of a chunk no face-adjacent rule reaches.</summary>
    internal static void AddTouchedChunks(int x, int y, int z, HashSet<long> into)
    {
        var cx = x >> Chunk.SizeShift;
        var cy = y >> Chunk.SizeShift;
        var cz = z >> Chunk.SizeShift;
        into.Add(ChunkKey.Pack(cx, cy, cz));

        var lx = x & Chunk.SizeMask;
        var ly = y & Chunk.SizeMask;
        var lz = z & Chunk.SizeMask;

        var dx = lx == 0 ? -1 : lx == Chunk.SizeMask ? 1 : 0;
        var dy = ly == 0 ? -1 : ly == Chunk.SizeMask ? 1 : 0;
        var dz = lz == 0 ? -1 : lz == Chunk.SizeMask ? 1 : 0;

        if (dx != 0) into.Add(ChunkKey.Pack(cx + dx, cy, cz));
        if (dy != 0) into.Add(ChunkKey.Pack(cx, cy + dy, cz));
        if (dz != 0) into.Add(ChunkKey.Pack(cx, cy, cz + dz));

        if (dy == 0) return;
        if (dx != 0) into.Add(ChunkKey.Pack(cx + dx, cy + dy, cz));
        if (dz != 0) into.Add(ChunkKey.Pack(cx, cy + dy, cz + dz));
    }
}
