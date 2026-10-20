using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace VoxelEngine.Chunks;

/// <summary>The world cells the player built, bucketed by chunk, and the queue of restored chunks whose saved placements still have to be adopted into it. Owned by <see cref="ChunkManager"/>; main thread only except <see cref="EnqueueRestored"/>, which the generation workers call.</summary>
internal sealed class PlayerPlacedIndex(ChunkMap chunks)
{
    // Chunks restored from the save store whose player-placed cells still have to be adopted
    // into _playerPlacedByChunk. Filled by the generation workers (CreateChunk), drained on the
    // main thread at the top of Update, since the placement index is main-thread only. Like
    // _loadedChunks, an entry can outlive an unload, so the drain re-checks the chunk is live.
    private readonly ConcurrentQueue<Chunk> _restoredChunks = new();

    // World cells the player built (integer coords): breaking one never fells a tree, it just
    // drops as-is with no toppling entity. Main thread only, like every other block edit, so
    // this stays a plain Dictionary/HashSet pair — AGENTS.md reserves the concurrent
    // collections for genuinely shared state, and nothing off the main thread reads this.
    // A chunk's entries are purged on unload to keep the index — and the felling decision —
    // truthful. With a save store attached they are first written into the chunk's snapshot
    // (PlacedIndicesOf), and a restored chunk brings them back through _restoredChunks; with no
    // store the chunk regenerates from worldgen and its placements are simply forgotten.
    // Bucketing by chunk position is what makes that purge cheap: the unload drops one bucket instead of
    // scanning every placement in the world, which was O(unloads x placements) per unload pass
    // (PERF_TODO C6). The key is the same packed chunk key Chunks is keyed on (ChunkKey, see
    // _dirtyChunks above), so the unload site already holds it. A bucket is removed as soon as
    // it empties; without that the index would grow one permanent entry per chunk ever edited,
    // trading the scan for a slow leak.
    private readonly Dictionary<long, HashSet<(int X, int Y, int Z)>> _playerPlacedByChunk = new();

    /// <summary>Whether the world cell was built by the player rather than generated.</summary>
    public bool IsPlayerPlaced(int x, int y, int z) =>
        _playerPlacedByChunk.TryGetValue(ChunkKey.PackBlock(x, y, z), out var cells) &&
        cells.Contains((x, y, z));

    /// <summary>Records a player placement (called by <see cref="Chunk.PlaceBlockAt"/>).</summary>
    public void MarkPlayerPlaced(int x, int y, int z)
    {
        var chunkKey = ChunkKey.PackBlock(x, y, z);
        if (!_playerPlacedByChunk.TryGetValue(chunkKey, out var cells))
        {
            cells = new HashSet<(int X, int Y, int Z)>();
            _playerPlacedByChunk[chunkKey] = cells;
        }

        cells.Add((x, y, z));
    }

    /// <summary>Forgets a placement once its block is gone (mined, felled or blasted).</summary>
    public void UnmarkPlayerPlaced(int x, int y, int z)
    {
        // Most callers unmark unconditionally on any block removal, so the miss path has to
        // stay a lookup that allocates nothing.
        var chunkKey = ChunkKey.PackBlock(x, y, z);
        if (!_playerPlacedByChunk.TryGetValue(chunkKey, out var cells)) return;

        cells.Remove((x, y, z));
        if (cells.Count == 0) _playerPlacedByChunk.Remove(chunkKey);
    }

    /// <summary>
    /// How many chunks currently hold at least one player placement. Diagnostics and tests
    /// only: it exists so the empty-bucket invariant above is pinnable.
    /// </summary>
    internal int PlayerPlacedChunkCount => _playerPlacedByChunk.Count;

    /// <summary>
    /// Moves the player-placed cells of every chunk restored since the last call into the
    /// placement index, then clears them off the chunk (adopted). Main thread only. A chunk
    /// that was unloaded (or replaced) before the drain reached it is skipped: its snapshot
    /// already carried the placements back to the store.
    /// </summary>
    internal void AdoptRestoredPlacements()
    {
        while (_restoredChunks.TryDequeue(out var chunk))
        {
            if (!chunks.TryGetPacked(ChunkKey.Pack(chunk.Position), out var live) || live != chunk)
                continue;

            var placed = chunk.RestoredPlayerPlaced;
            if (placed == null) continue;

            var min = chunk.WorldMin;
            foreach (var index in placed)
            {
                var cell = min + Chunk.LocalOfIdx(index);
                MarkPlayerPlaced((int)cell.X, (int)cell.Y, (int)cell.Z);
            }

            chunk.RestoredPlayerPlaced = null;
        }
    }

    /// <summary>
    /// The chunk's player-placed cells as local <see cref="Chunk.Idx"/> indices for its
    /// snapshot: the adopted ones from the placement index plus any the chunk still holds from
    /// its restore and has not handed over yet. Null when there are none.
    /// </summary>
    internal ushort[]? PlacedIndicesOf(Chunk chunk, long key)
    {
        _playerPlacedByChunk.TryGetValue(key, out var cells);
        var pending = chunk.RestoredPlayerPlaced;
        if ((cells == null || cells.Count == 0) && (pending == null || pending.Length == 0)) return null;

        var minX = (int)chunk.WorldMin.X;
        var minY = (int)chunk.WorldMin.Y;
        var minZ = (int)chunk.WorldMin.Z;
        var indices = new HashSet<ushort>();
        if (cells != null)
        {
            foreach (var (x, y, z) in cells)
                indices.Add((ushort)Chunk.Idx(x - minX, y - minY, z - minZ));
        }
        if (pending != null)
        {
            foreach (var index in pending) indices.Add(index);
        }

        var result = new ushort[indices.Count];
        indices.CopyTo(result);
        Array.Sort(result);
        return result;
    }

    /// <summary>Queues a chunk restored from the save store for <see cref="AdoptRestoredPlacements"/>. Called on a generation worker, only once the chunk is published.</summary>
    internal void EnqueueRestored(Chunk chunk) => _restoredChunks.Enqueue(chunk);

    /// <summary>Drops the placement bucket of a chunk that is unloading.</summary>
    internal void ForgetChunk(long key) => _playerPlacedByChunk.Remove(key);

    /// <summary>Empties the restore queue and the index, for <see cref="ChunkManager.ReleaseAll"/>.</summary>
    internal void Release()
    {
        while (_restoredChunks.TryDequeue(out _)) { }
        _playerPlacedByChunk.Clear();
    }
}
