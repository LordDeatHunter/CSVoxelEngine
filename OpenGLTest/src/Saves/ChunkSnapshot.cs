using System;
using VoxelEngine.Chunks;

namespace VoxelEngine.Saves;

/// <summary>
/// An immutable copy of one chunk's saveable data: the block ids (as a palette-compressed
/// <see cref="ChunkBlocks"/>), the per-block state byte and the cells the player placed. Taken on
/// the main thread by <see cref="Chunk.TakeSnapshot"/> (a <see cref="ChunkBlocks.Clone"/> of the
/// storage and plain array copies, no encoding), encoded later by the save writer thread through
/// <see cref="ChunkSaveCodec"/>, and handed to <see cref="Chunk.RestoreFrom(ChunkSnapshot)"/> when the
/// chunk streams back in. Nothing writes a snapshot after construction: the public constructor
/// copies every array it is given, and the internal one takes over arrays nobody else holds. The
/// storage and arrays the properties expose must be treated as read-only: a snapshot can be read
/// by the writer thread and a generation worker at the same time, which is only safe because
/// nobody writes to it, and every consumer that needs a mutable copy
/// (<see cref="Chunk.RestoreFrom(ChunkSnapshot)"/>) makes one.
/// </summary>
public sealed class ChunkSnapshot
{
    /// <param name="blocks">The block ids in <see cref="Chunk.Idx"/> order; exactly
    /// <see cref="Chunk.BlockCount"/> long. Compacted into the snapshot's own storage, never kept.</param>
    /// <param name="state">The state bytes in the same order, or null when every cell is 0.</param>
    /// <param name="playerPlaced">Chunk-local <see cref="Chunk.Idx"/> indices of the
    /// player-placed cells, or null (an empty array is stored as null) when there are none.</param>
    public ChunkSnapshot(ushort[] blocks, byte[]? state, ushort[]? playerPlaced)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Length != Chunk.BlockCount)
            throw new ArgumentException(
                $"A chunk snapshot needs {Chunk.BlockCount} blocks, got {blocks.Length}.", nameof(blocks));
        if (state != null && state.Length != Chunk.BlockCount)
            throw new ArgumentException(
                $"A chunk snapshot needs {Chunk.BlockCount} state bytes, got {state.Length}.", nameof(state));

        Storage = ChunkBlocks.FromDense(blocks);
        State = (byte[]?)state?.Clone();
        PlayerPlaced = playerPlaced is { Length: > 0 } ? (ushort[])playerPlaced.Clone() : null;
    }

    /// <summary>
    /// Takes ownership of <paramref name="storage"/>, <paramref name="state"/> and
    /// <paramref name="playerPlaced"/> without copying: the caller hands over a storage and arrays
    /// nobody else writes (a fresh <see cref="ChunkBlocks.Clone"/> or decode, or another snapshot's
    /// read-only ones). An empty <paramref name="playerPlaced"/> is stored as null.
    /// </summary>
    internal ChunkSnapshot(ChunkBlocks storage, byte[]? state, ushort[]? playerPlaced)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (state != null && state.Length != Chunk.BlockCount)
            throw new ArgumentException(
                $"A chunk snapshot needs {Chunk.BlockCount} state bytes, got {state.Length}.", nameof(state));

        Storage = storage;
        State = state;
        PlayerPlaced = playerPlaced is { Length: > 0 } ? playerPlaced : null;
    }

    /// <summary>The snapshot's own block storage, never written after construction (a Uniform one
    /// may be shared with other snapshots and chunks, since Uniform storages are immutable).</summary>
    internal ChunkBlocks Storage { get; }

    /// <summary>The state bytes in <see cref="Chunk.Idx"/> order, or null when every cell's state
    /// is 0. Read-only by contract.</summary>
    public byte[]? State { get; }

    /// <summary>The player-placed cells as chunk-local <see cref="Chunk.Idx"/> indices, or null
    /// when there are none. Read-only by contract.</summary>
    public ushort[]? PlayerPlaced { get; }
}
