using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Simulation.LeafDecay;

/// <summary>The leaf decay's cell access for one frame: reads and writes through a direct-mapped chunk cache, the chunks its removals touch for one MarkDirty batch, and the 21-bit cell-key packer. Owned by <see cref="LeafDecayManager"/>; main thread only.</summary>
internal sealed class LeafDecayCells
{
    // Cell keys pack a world position into 21 bits per axis (±1,048,576 blocks), as in
    // FluidManager.
    private const int PackBits = 21;
    private const long PackMask = (1L << PackBits) - 1;

    // Chunks touched by this frame's removals, remeshed once the frame's slice is done
    private readonly HashSet<long> _dirtyChunks = [];

    // The manager this frame's reads and writes go through; set by BeginFrame before the ticks run
    private ChunkManager _chunks = null!;

    // Direct-mapped chunk-lookup cache for one frame, as in FluidManager: keys cleared at the
    // top of every Update, only the Chunk cached (never its arrays, which SetBlockRaw may swap)
    private const int ChunkCacheSize = 16;
    private const int ChunkCacheMask = ChunkCacheSize - 1;
    private readonly Chunk?[] _chunkCache = new Chunk?[ChunkCacheSize];
    private readonly long[] _chunkCacheKeys = new long[ChunkCacheSize];

    /// <summary>Binds the frame's chunk manager and empties the chunk cache; called by Update before the ticks run.</summary>
    internal void BeginFrame(ChunkManager chunks)
    {
        _chunks = chunks;

        // The chunk cache is valid for one frame only: an unload can replace a chunk between
        // frames, and a reference cached before it must never be handed back
        _chunkCacheKeys.AsSpan().Fill(-1L);
    }

    /// <summary>Queues every chunk this frame's removals touched for one remesh, then forgets them.</summary>
    internal void FlushDirty(ChunkManager chunks)
    {
        if (_dirtyChunks.Count > 0)
        {
            foreach (var key in _dirtyChunks)
            {
                Unpack(key, out var cx, out var cy, out var cz);
                chunks.MarkDirty(new Vector3(cx, cy, cz));
            }
            _dirtyChunks.Clear();
        }
    }

    /// <summary>
    /// Writes a cell's id and state through the frame's chunk cache. A hydration rewrite passes
    /// <paramref name="remesh"/> false: the mesher never reads <see cref="LeafState"/>, so
    /// nothing visible changes. A removal queues only the cell's own chunk and the chunks across
    /// a border it touches, never the light radius: leaves and tree wood both pass light, cast
    /// no ambient occlusion and occlude nothing, so taking one out changes no other cell's light.
    /// </summary>
    internal void Write(int x, int y, int z, byte id, byte state, bool remesh)
    {
        if (!TryGetChunk(x, y, z, out var chunk)) return;

        chunk!.SetBlockRaw(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask, id, state);
        if (remesh) AddTouchedChunks(x, y, z, _dirtyChunks);
    }

    /// <summary>
    /// The loaded chunk owning a cell, through the frame's direct-mapped cache; the miss path
    /// goes through <see cref="ChunkManager.ChunkAt"/>, so a chunk without ready block data
    /// reads as unloaded.
    /// </summary>
    private bool TryGetChunk(int x, int y, int z, out Chunk? chunk)
    {
        var key = Pack(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);
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
    /// Reads a world cell's block id and state through the frame's chunk cache. False means the
    /// chunk is not loaded, and an unloaded cell must never read as air.
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

    /// <summary>Adds the chunk owning a cell, plus the face-adjacent chunk on every axis where
    /// the cell sits on a chunk border, to <paramref name="into"/> as packed chunk keys.</summary>
    private static void AddTouchedChunks(int x, int y, int z, HashSet<long> into)
    {
        var cx = x >> Chunk.SizeShift;
        var cy = y >> Chunk.SizeShift;
        var cz = z >> Chunk.SizeShift;
        into.Add(Pack(cx, cy, cz));

        var lx = x & Chunk.SizeMask;
        var ly = y & Chunk.SizeMask;
        var lz = z & Chunk.SizeMask;

        if (lx == 0) into.Add(Pack(cx - 1, cy, cz));
        else if (lx == Chunk.SizeMask) into.Add(Pack(cx + 1, cy, cz));

        if (ly == 0) into.Add(Pack(cx, cy - 1, cz));
        else if (ly == Chunk.SizeMask) into.Add(Pack(cx, cy + 1, cz));

        if (lz == 0) into.Add(Pack(cx, cy, cz - 1));
        else if (lz == Chunk.SizeMask) into.Add(Pack(cx, cy, cz + 1));
    }

    internal static long Pack(int x, int y, int z) =>
        ((x & PackMask) << (2 * PackBits)) | ((y & PackMask) << PackBits) | (z & PackMask);

    internal static void Unpack(long key, out int x, out int y, out int z)
    {
        x = SignExtend((int)((key >> (2 * PackBits)) & PackMask));
        y = SignExtend((int)((key >> PackBits) & PackMask));
        z = SignExtend((int)(key & PackMask));
    }

    // A packed axis keeps only the low PackBits, so its sign bit has to be pushed back up
    private static int SignExtend(int value) => (value << (32 - PackBits)) >> (32 - PackBits);
}
