using System;
using VoxelEngine.Chunks;

namespace VoxelEngine.Saves;

/// <summary>
/// An immutable copy of one chunk's saveable data: the block ids, the per-block state byte and
/// the cells the player placed. Taken on the main thread by <see cref="Chunk.TakeSnapshot"/>
/// (plain array copies, no encoding), encoded later by the save writer thread through
/// <see cref="ChunkSaveCodec"/>, and handed to <see cref="Chunk.RestoreFrom"/> when the chunk
/// streams back in. The constructor copies every array it is given, so nothing the caller does
/// afterwards can change the snapshot. The arrays the properties expose are the snapshot's own
/// and must be treated as read-only: a snapshot can be read by the writer thread and a
/// generation worker at the same time, which is only safe because nobody writes to it, and every
/// consumer that needs a mutable array (<see cref="Chunk.RestoreFrom"/>) copies it.
/// </summary>
public sealed class ChunkSnapshot
{
    /// <param name="blocks">The block ids in <see cref="Chunk.Idx"/> order; exactly
    /// <see cref="Chunk.BlockCount"/> long.</param>
    /// <param name="state">The state bytes in the same order, or null when every cell is 0.</param>
    /// <param name="playerPlaced">Chunk-local <see cref="Chunk.Idx"/> indices of the
    /// player-placed cells, or null (an empty array is stored as null) when there are none.</param>
    public ChunkSnapshot(byte[] blocks, byte[]? state, ushort[]? playerPlaced)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Length != Chunk.BlockCount)
            throw new ArgumentException(
                $"A chunk snapshot needs {Chunk.BlockCount} blocks, got {blocks.Length}.", nameof(blocks));
        if (state != null && state.Length != Chunk.BlockCount)
            throw new ArgumentException(
                $"A chunk snapshot needs {Chunk.BlockCount} state bytes, got {state.Length}.", nameof(state));

        Blocks = (byte[])blocks.Clone();
        State = (byte[]?)state?.Clone();
        PlayerPlaced = playerPlaced is { Length: > 0 } ? (ushort[])playerPlaced.Clone() : null;
    }

    /// <summary>The block ids, <see cref="Chunk.BlockCount"/> of them in <see cref="Chunk.Idx"/>
    /// order. Read-only by contract.</summary>
    public byte[] Blocks { get; }

    /// <summary>The state bytes in the same order, or null when every cell's state is 0.
    /// Read-only by contract.</summary>
    public byte[]? State { get; }

    /// <summary>The player-placed cells as chunk-local <see cref="Chunk.Idx"/> indices, or null
    /// when there are none. Read-only by contract.</summary>
    public ushort[]? PlayerPlaced { get; }
}
