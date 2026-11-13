using VoxelEngine.Effects.Climate;

namespace VoxelEngine.Map;

/// <summary>Whether a chunk can be read by the map: not loaded, loaded but all air, or holding blocks.</summary>
public enum MapChunkState : byte
{
    Unloaded,
    Empty,
    Blocks,
}

/// <summary>
/// What the map scanner reads of the world: a chunk's state, a cell's block id and a chunk column's biome tint fields.
/// <see cref="ChunkMapCells"/> reads the live chunks; tests pass a fake. Main thread only.
/// </summary>
public interface IMapCells
{
    /// <summary>The state of chunk (chunkX, chunkY, chunkZ).</summary>
    MapChunkState StateOf(int chunkX, int chunkY, int chunkZ);

    /// <summary>The block id at a world cell whose chunk is loaded.</summary>
    ushort BlockAt(int x, int y, int z);

    /// <summary>Chunk column (chunkX, chunkZ)'s grass and foliage tint fields, the ones its mesh wears; false when a
    /// streamed column has not arrived.</summary>
    bool TryTintFields(int chunkX, int chunkZ, out BiomeTintFields fields);
}
