namespace VoxelEngine.Chunks;

/// <summary>
/// Where a chunk column's <see cref="ColumnTerrain"/> comes from: worldgen for singleplayer and
/// the server (<see cref="WorldgenColumnSource"/>), the streamed table on a multiplayer client.
/// Thread-safe: called from pool mesh builds and from the main thread.
/// </summary>
public interface IColumnSource
{
    /// <summary>
    /// Whether <see cref="TryGet"/> would succeed for chunk column (chunkX, chunkZ). Cheap (no
    /// computation, no allocation): it is what the dirty gate asks per frame. The worldgen source
    /// always answers true.
    /// </summary>
    bool IsReady(int chunkX, int chunkZ);

    /// <summary>
    /// The terrain of chunk column (chunkX, chunkZ). False only when the column is not available,
    /// i.e. a streamed column that has not arrived yet; the worldgen source always succeeds.
    /// </summary>
    bool TryGet(int chunkX, int chunkZ, out ColumnTerrain terrain);
}
