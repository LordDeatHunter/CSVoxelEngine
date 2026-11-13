using VoxelEngine.Chunks;
using VoxelEngine.Effects.Climate;

namespace VoxelEngine.Map;

/// <summary>
/// The map's view of the live chunks of a <see cref="ChunkManager"/>. The scanner walks down one chunk at a time, so
/// the last chunk looked up is kept and most cell reads skip the dictionary; a scan starts with the chunk states, and
/// any state query drops that cache, so a chunk unloaded between two scans is never read. Main thread only.
/// </summary>
public sealed class ChunkMapCells(ChunkManager chunks) : IMapCells
{
    private Chunk? _last;
    private int _lastX, _lastY, _lastZ;

    public MapChunkState StateOf(int chunkX, int chunkY, int chunkZ)
    {
        _last = null;
        if (!chunks.Chunks.TryGetValue(chunkX, chunkY, chunkZ, out var chunk) || !chunk.IsBlockDataReady)
            return MapChunkState.Unloaded;
        return ReferenceEquals(chunk.Storage, ChunkBlocks.Air) ? MapChunkState.Empty : MapChunkState.Blocks;
    }

    public ushort BlockAt(int x, int y, int z)
    {
        var cx = x >> Chunk.SizeShift;
        var cy = y >> Chunk.SizeShift;
        var cz = z >> Chunk.SizeShift;
        if (_last == null || cx != _lastX || cy != _lastY || cz != _lastZ)
        {
            _last = chunks.ChunkAt(x, y, z);
            (_lastX, _lastY, _lastZ) = (cx, cy, cz);
        }
        return _last?.GetLocalBlock(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask) ?? BlockType.Air;
    }

    public bool TryTintFields(int chunkX, int chunkZ, out BiomeTintFields fields) =>
        BiomeTint.TryFieldsFor(chunks.ColumnSource, chunkX, chunkZ, out fields);
}
