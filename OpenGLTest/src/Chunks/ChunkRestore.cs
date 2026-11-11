using System;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;
using static VoxelEngine.Chunks.Chunk;

namespace VoxelEngine.Chunks;

/// <summary>Rebuilds a chunk from a <see cref="ChunkSnapshot"/> instead of generating it (a save, a world source's map, a streamed chunk), behind <see cref="Chunk.RestoreFrom(ChunkSnapshot)"/>.</summary>
internal static class ChunkRestore
{
    /// <summary>
    /// Fills <paramref name="chunk"/> from a save instead of generating it: the blocks, the state bytes and
    /// the player-placed cells (handed to the chunk manager through
    /// <see cref="Chunk.RestoredPlayerPlaced"/>) come from <paramref name="snapshot"/>, and everything
    /// <see cref="Chunk.GenerateBlockData"/> derives on the way — the non-air count, the surface
    /// floor and the fluid frontier — is recomputed from them. Runs on a generation worker on a
    /// chunk built with <c>generateBlockData: false</c>, before any other thread can see it;
    /// <see cref="Chunk.IsBlockDataReady"/> goes true last, which is what publishes the rest.
    /// </summary>
    internal static void RestoreFrom(Chunk chunk, ChunkSnapshot snapshot) => RestoreFrom(chunk, snapshot, streamedColumns: false);

    /// <summary>
    /// <see cref="RestoreFrom(Chunk, ChunkSnapshot)"/> with the block source chosen: a streamed
    /// multiplayer chunk (<paramref name="streamedColumns"/> true) carries no worldgen column, so
    /// its surface floor is derived from its own cells and no fluid frontier is scanned (the server
    /// owns fluids). A save restore without a world source at hand uses the column cache as
    /// before, for the fluid frontier (<see cref="RestoreFrom(Chunk, ChunkSnapshot, ColumnData)"/>).
    /// </summary>
    internal static void RestoreFrom(Chunk chunk, ChunkSnapshot snapshot, bool streamedColumns)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!streamedColumns)
        {
            RestoreFrom(chunk, snapshot, ColumnCache.Get((int)chunk.Position.X, (int)chunk.Position.Z));
            return;
        }

        var dense = CopySnapshot(chunk, snapshot);
        chunk.SurfaceMinY = LowestSolidY(chunk, dense);
        FinishRestore(chunk, snapshot);
    }

    /// <summary>
    /// <see cref="RestoreFrom(Chunk, ChunkSnapshot)"/> against a known column: the surface floor comes
    /// from <paramref name="column"/> and the fluid frontier is scanned against its heights and
    /// water levels. This is the save store's restore (with the world source's column) and the
    /// way a world source that supplies chunks fills one (<see cref="IWorldSource.Fill"/>).
    /// </summary>
    internal static void RestoreFrom(Chunk chunk, ChunkSnapshot snapshot, ColumnData column)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(column);

        var dense = CopySnapshot(chunk, snapshot);
        chunk.SurfaceMinY = column.MinHeight;
        // A restored chunk can hold fluid anywhere, so it always scans (a chunk with no fluid
        // cell simply reports nothing). An all-air chunk has nothing to scan; any other uniform
        // chunk is spelled out in the dense scratch so the scan stays the one fluid rule
        if (dense == null && chunk.Storage.Get(0) == BlockType.Air)
            chunk.FluidFrontier = null;
        else
            ChunkTerrainFill.ScanFluidFrontier(chunk, dense ?? UniformDense(chunk.Storage.Get(0)), column, chunk.LowerBoundY);

        FinishRestore(chunk, snapshot);
    }

    // The thread's dense scratch filled with one id, for the scans that want dense cells
    private static ushort[] UniformDense(ushort id)
    {
        var dense = ChunkBlocks.DenseScratch;
        dense.AsSpan().Fill(id);
        return dense;
    }

    // The blocks, state bytes and non-air count of a restore. Returns the cells decoded into the
    // thread's dense scratch (valid until the caller's restore ends), or null for a uniform
    // snapshot, whose immutable storage the chunk shares instead. Never aliases a snapshot's
    // mutable storage: a compact one (anything a codec decoded) is cloned, an edited one
    // (a TakeSnapshot of a chunk with stale palette entries) is recompacted.
    private static ushort[]? CopySnapshot(Chunk chunk, ChunkSnapshot snapshot)
    {
        var source = snapshot.Storage;
        ushort[]? dense = null;
        if (source.Mode == ChunkBlocks.StorageMode.Uniform)
        {
            chunk.Storage = source;
        }
        else
        {
            dense = ChunkBlocks.DenseScratch;
            source.CopyRun(0, BlockCount, dense);
            chunk.Storage = source.IsCompact ? source.Clone() : ChunkBlocks.FromDense(dense);
        }

        var storage = chunk.Storage;
        chunk.StateArray = snapshot.State is { } state ? (byte[])state.Clone() : null;
        chunk.SolidCount = storage.CountNonAir();
        // From the blocks alone, so a save, an imported map and a client's streamed copy all agree
        chunk.MayHaveSkyCover = BlockType.ContainsSkyCover(storage.IdSpan);
        return dense;
    }

    // The tail every restore shares; IsBlockDataReady goes true last, which publishes the rest.
    private static void FinishRestore(Chunk chunk, ChunkSnapshot snapshot)
    {
        chunk.IsRestored = true;
        chunk.RestoredPlayerPlaced = snapshot.PlayerPlaced is { } placed ? (ushort[])placed.Clone() : null;

        chunk.IsBlockDataReady = true;
    }

    // The world Y of this chunk's lowest non-air cell, or int.MinValue when it is all air. Reads the
    // dense cells CopySnapshot decoded, or the chunk's uniform storage when it decoded none.
    private static int LowestSolidY(Chunk chunk, ushort[]? blocks)
    {
        if (blocks == null)
            return chunk.Storage.Get(0) == BlockType.Air ? int.MinValue : chunk.LowerBoundY;

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        for (var z = 0; z < Size; z++)
            if (blocks[Idx(x, y, z)] != 0)
                return chunk.LowerBoundY + y;
        return int.MinValue;
    }
}
