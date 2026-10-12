using VoxelEngine.WorldGen;

namespace VoxelEngine.Chunks;

/// <summary>
/// The default <see cref="IWorldSource"/>: worldgen, exactly as before world sources existed.
/// <see cref="GetColumn"/> is <see cref="ColumnCache.Get"/> itself (the cached instance, no copy,
/// no allocation), <see cref="Supplies"/> is always false and <see cref="Fill"/> is
/// <see cref="Chunk.GenerateBlockData"/>, so a world on this source makes the same reads in the
/// same order as the old path and its terrain is bit for bit the same.
/// </summary>
public sealed class GeneratedWorldSource : IWorldSource
{
    /// <summary>The one instance; the source is stateless.</summary>
    public static readonly GeneratedWorldSource Instance = new();

    private GeneratedWorldSource()
    {
    }

    /// <inheritdoc/>
    public ColumnData GetColumn(int chunkX, int chunkZ) => ColumnCache.Get(chunkX, chunkZ);

    /// <inheritdoc/>
    public bool Supplies(int chunkX, int chunkZ) => false;

    /// <inheritdoc/>
    public void Fill(Chunk chunk) => chunk.GenerateBlockData();

    /// <inheritdoc/>
    public bool IsReady(int chunkX, int chunkZ) => true;

    /// <inheritdoc/>
    public bool TryGet(int chunkX, int chunkZ, out ColumnTerrain terrain) =>
        WorldgenColumnSource.Instance.TryGet(chunkX, chunkZ, out terrain);
}
