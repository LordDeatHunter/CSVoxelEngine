namespace VoxelEngine.WorldGen.Caves;

/// <summary>
/// Where a rock cell sits against the open cave around it (<see cref="CaveBiomes.RoleAt"/>), which picks
/// the palette block it wears.
/// </summary>
public enum CaveCellRole
{
    /// <summary>No open neighbor inside the chunk: buried rock.</summary>
    Interior,

    /// <summary>Open beside it, but neither above nor below.</summary>
    Wall,

    /// <summary>Open above it: a cave floor.</summary>
    Floor,

    /// <summary>Open below it (and not above): a cave ceiling.</summary>
    Ceiling,
}
