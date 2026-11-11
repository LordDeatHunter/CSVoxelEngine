namespace VoxelEngine.Simulation.Waystones;

/// <summary>
/// Which realm a waystone cell lies in, GL-free: hell below the barrier band (<see cref="WorldGenerationConfig.HellTopY"/>
/// and down), the sky from the island band up (<see cref="WorldGenerationConfig.SkyIslandBottomY"/>), and between them
/// the overworld, underground when the cell lies at least <see cref="UndergroundDepthBlocks"/> under its column's surface
/// (a crypt sits far deeper, a surface waystone stands one cell over the ground). Without the column's surface height (a
/// streamed column not in yet) a cell that far under sea level counts as underground.
/// </summary>
public static class WaystoneRealms
{
    /// <summary>How far under its column's surface a cell must lie to count as underground.</summary>
    public const int UndergroundDepthBlocks = 4;

    /// <summary>The realm of a waystone at world height <paramref name="y"/>; <paramref name="surfaceHeight"/> is the
    /// top solid cell of its column, or null when unknown.</summary>
    public static WaystoneRealm Classify(int y, int? surfaceHeight)
    {
        if (y <= WorldGenerationConfig.HellTopY) return WaystoneRealm.Hell;
        if (y >= WorldGenerationConfig.SkyIslandBottomY) return WaystoneRealm.Sky;
        var ground = surfaceHeight ?? WorldGenerationConfig.SeaLevel;
        return y <= ground - UndergroundDepthBlocks ? WaystoneRealm.Underground : WaystoneRealm.Surface;
    }

    /// <summary>The short tag the teleport list shows for a realm.</summary>
    public static string Tag(WaystoneRealm realm) => realm switch
    {
        WaystoneRealm.Underground => "underground",
        WaystoneRealm.Hell => "hell",
        WaystoneRealm.Sky => "sky",
        _ => "surface",
    };
}
