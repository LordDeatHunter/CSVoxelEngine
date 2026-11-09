using System;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;
using static VoxelEngine.Chunks.Chunk;

namespace VoxelEngine.Chunks;

/// <summary>Rebuilds a chunk from a <see cref="ChunkSnapshot"/> instead of generating it (a save, a world source's map, a streamed chunk), behind <see cref="Chunk.RestoreFrom(ChunkSnapshot)"/>.</summary>
internal static class ChunkRestore
{
    /// <inheritdoc cref="Chunk.RestoreFrom(ChunkSnapshot)"/>
    internal static void RestoreFrom(Chunk chunk, ChunkSnapshot snapshot) => RestoreFrom(chunk, snapshot, streamedColumns: false);

    /// <inheritdoc cref="Chunk.RestoreFrom(ChunkSnapshot, bool)"/>
    internal static void RestoreFrom(Chunk chunk, ChunkSnapshot snapshot, bool streamedColumns)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!streamedColumns)
        {
            RestoreFrom(chunk, snapshot, ColumnCache.Get((int)chunk.Position.X, (int)chunk.Position.Z));
            return;
        }

        CopySnapshot(chunk, snapshot);
        chunk.SurfaceMinY = LowestSolidY(chunk);
        FinishRestore(chunk, snapshot);
    }

    /// <inheritdoc cref="Chunk.RestoreFrom(ChunkSnapshot, ColumnData)"/>
    internal static void RestoreFrom(Chunk chunk, ChunkSnapshot snapshot, ColumnData column)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(column);

        CopySnapshot(chunk, snapshot);
        chunk.SurfaceMinY = column.MinHeight;
        // A restored chunk can hold fluid anywhere, so it always scans (a chunk with no fluid
        // cell simply reports nothing)
        ChunkTerrainFill.ScanFluidFrontier(chunk, column, chunk.LowerBoundY);
        FinishRestore(chunk, snapshot);
    }

    // The blocks, state bytes and non-air count of a restore.
    private static void CopySnapshot(Chunk chunk, ChunkSnapshot snapshot)
    {
        Array.Copy(snapshot.Blocks, chunk.Blocks, BlockCount);
        chunk.StateArray = snapshot.State is { } state ? (byte[])state.Clone() : null;
        chunk.SolidCount = BlockCount - chunk.Blocks.AsSpan().Count(BlockType.Air);
        // From the blocks alone, so a save, an imported map and a client's streamed copy all agree
        chunk.MayHaveSkyCover = BlockType.ContainsSkyCover(chunk.Blocks);
    }

    // The tail every restore shares; IsBlockDataReady goes true last, which publishes the rest.
    private static void FinishRestore(Chunk chunk, ChunkSnapshot snapshot)
    {
        chunk.IsRestored = true;
        chunk.RestoredPlayerPlaced = snapshot.PlayerPlaced is { } placed ? (ushort[])placed.Clone() : null;

        chunk.IsBlockDataReady = true;
    }

    // The world Y of this chunk's lowest non-air cell, or int.MinValue when it is all air.
    private static int LowestSolidY(Chunk chunk)
    {
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        for (var z = 0; z < Size; z++)
            if (chunk.Blocks[Idx(x, y, z)] != 0)
                return chunk.LowerBoundY + y;
        return int.MinValue;
    }
}
