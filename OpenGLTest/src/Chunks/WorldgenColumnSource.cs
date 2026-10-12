namespace silkgltriangle;

/// <summary>
/// The singleplayer and server <see cref="IColumnSource"/>: every column straight from the
/// worldgen column cache (<see cref="ColumnCache.Get"/>), always available. It is the one place
/// the mesh path reaches worldgen.
/// </summary>
public sealed class WorldgenColumnSource : IColumnSource
{
    /// <summary>The one instance; the source is stateless.</summary>
    public static readonly WorldgenColumnSource Instance = new();

    private WorldgenColumnSource()
    {
    }

    /// <inheritdoc/>
    public bool IsReady(int chunkX, int chunkZ) => true;

    /// <inheritdoc/>
    public bool TryGet(int chunkX, int chunkZ, out ColumnTerrain terrain)
    {
        var column = ColumnCache.Get(chunkX, chunkZ);
        terrain = new ColumnTerrain(column.Heights, column.WaterLevel, column.CornerWarmth, column.CornerWetness);
        return true;
    }
}
