namespace VoxelEngine.WorldGen;

/// <summary>
/// The verdict of one great-lake cell (<see cref="GreatLakes.SiteAt"/>). <c>default</c>
/// (<see cref="Exists"/> false) is a cell with no lake: its roll missed, or the candidate was rejected.
/// An accepted lake has a water <see cref="Surface"/> chosen from its rim (or
/// <see cref="WorldGenerationConfig.SeaLevel"/> when <see cref="RiverFed"/>), a bowl
/// <see cref="Floor"/> below it, the share of each lobe radius its shore ramp takes
/// (<see cref="ShoreShare"/>), 1 to 3 lobe discs (<see cref="LobeCount"/>; <see cref="Lobe0"/> is the
/// main lobe at the cell's rolled center) and an optional <see cref="Island"/> inside the main lobe
/// (radius 0 when it has none). <see cref="Reach"/> is the largest distance from
/// <see cref="Lobe0"/>'s center to any lobe edge.
/// </summary>
internal readonly record struct GreatLakeSite(
    bool Exists,
    int Surface,
    int Floor,
    float ShoreShare,
    bool RiverFed,
    int LobeCount,
    float Reach,
    GreatLakeLobe Lobe0,
    GreatLakeLobe Lobe1,
    GreatLakeLobe Lobe2,
    GreatLakeLobe Island)
{
    /// <summary>Lobe <paramref name="k"/> (0 = <see cref="Lobe0"/>); only
    /// <paramref name="k"/> &lt; <see cref="LobeCount"/> is meaningful.</summary>
    internal GreatLakeLobe Lobe(int k) => k switch
    {
        0 => Lobe0,
        1 => Lobe1,
        _ => Lobe2,
    };
}
