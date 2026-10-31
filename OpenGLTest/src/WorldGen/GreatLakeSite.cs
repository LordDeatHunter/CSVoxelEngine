namespace VoxelEngine.WorldGen;

/// <summary>
/// The verdict of one great-lake cell (<see cref="GreatLakes.SiteAt"/>). <c>default</c>
/// (<see cref="Exists"/> false) is a cell with no lake: its roll missed, or the candidate was rejected.
/// An accepted lake has a water <see cref="Surface"/> chosen from its rim (a <see cref="RiverFed"/>
/// lake without a river ramp rests at <see cref="WorldGenerationConfig.SeaLevel"/> instead), a bowl
/// <see cref="Floor"/> below it, the share of each lobe radius its shore ramp takes
/// (<see cref="ShoreShare"/>), 1 to 3 lobe discs (<see cref="LobeCount"/>; <see cref="Lobe0"/> is the
/// main lobe at the cell's rolled center) and an optional <see cref="Island"/> inside the main lobe
/// (radius 0 when it has none). <see cref="Reach"/> is the largest distance from
/// <see cref="Lobe0"/>'s center to any lobe edge.
/// <para>
/// <see cref="RampLength"/> is 0 for a lake without a river ramp, else
/// (<see cref="Surface"/> - <see cref="WorldGenerationConfig.SeaLevel"/>) times
/// <see cref="WorldGenerationConfig.GreatLakeRiverRunBlocks"/>: how far past a lobe edge the river level
/// steps down from the surface to the sea. The outflow is a straight channel leaving the lake:
/// <see cref="OutflowX"/>/<see cref="OutflowZ"/> is the rim point where it crosses the shore,
/// (<see cref="OutflowDirX"/>, <see cref="OutflowDirZ"/>) the unit direction away from its lobe,
/// <see cref="OutflowStart"/> (negative) how far back from the rim point it reaches into standing water
/// and <see cref="OutflowLength"/> how far past the rim point it runs. <see cref="OutflowLength"/> 0
/// means no outflow, and an outflow exists only with a ramp.
/// </para>
/// </summary>
internal readonly record struct GreatLakeSite(
    bool Exists,
    int Surface,
    int Floor,
    float ShoreShare,
    bool RiverFed,
    float RampLength,
    float OutflowX,
    float OutflowZ,
    float OutflowDirX,
    float OutflowDirZ,
    float OutflowStart,
    float OutflowLength,
    int LobeCount,
    float Reach,
    GreatLakeLobe Lobe0,
    GreatLakeLobe Lobe1,
    GreatLakeLobe Lobe2,
    GreatLakeLobe Island)
{
    /// <summary>Whether the lake has an outflow channel (<see cref="OutflowLength"/> above 0).</summary>
    internal bool HasOutflow => OutflowLength > 0f;

    /// <summary>Lobe <paramref name="k"/> (0 = <see cref="Lobe0"/>); only
    /// <paramref name="k"/> &lt; <see cref="LobeCount"/> is meaningful.</summary>
    internal GreatLakeLobe Lobe(int k) => k switch
    {
        0 => Lobe0,
        1 => Lobe1,
        _ => Lobe2,
    };
}
