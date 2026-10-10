namespace silkgltriangle;

/// <summary>
/// Where a world's terrain comes from: the <see cref="ColumnData"/> of every chunk column and the
/// blocks of every chunk the save store does not hold. <see cref="GeneratedWorldSource"/> (the
/// default) is worldgen, unchanged; a server can host a world from another source, for example a
/// pre-built imported map, and its clients work unchanged because they never generate anything.
/// It extends <see cref="IColumnSource"/>, so the mesher's column reads and the server's column
/// answers read the same object.
/// </summary>
/// <remarks>
/// Thread-safe and deterministic: a source is called on the generation workers
/// (<see cref="ChunkManager.CreateChunk"/>), the pool mesh builds, the LodGen threads and the main
/// thread, and the same column or chunk must always come back the same. Never touches GL, and
/// never writes into a <see cref="GenerationalCache{TKey, TValue}"/>: those caches hold pure
/// functions of their key only, which a custom source's terrain is not.
/// </remarks>
public interface IWorldSource : IColumnSource
{
    /// <summary>
    /// The complete <see cref="ColumnData"/> of chunk column (<paramref name="chunkX"/>,
    /// <paramref name="chunkZ"/>): heights, bed, water levels, biomes, dirt depth and the corner
    /// climate. Never null.
    /// </summary>
    ColumnData GetColumn(int chunkX, int chunkZ);

    /// <summary>
    /// Whether this source supplies column (<paramref name="chunkX"/>, <paramref name="chunkZ"/>)
    /// and its whole chunk stack itself (true), or defers both to worldgen (false). Cheap: no
    /// computation, no allocation.
    /// </summary>
    bool Supplies(int chunkX, int chunkZ);

    /// <summary>
    /// Fills a chunk built with <c>generateBlockData: false</c>. On return
    /// <see cref="Chunk.IsBlockDataReady"/> is true and the surface floor, the non-air count and
    /// the fluid frontier are set, exactly as generating or restoring the chunk leaves them.
    /// </summary>
    void Fill(Chunk chunk);
}
