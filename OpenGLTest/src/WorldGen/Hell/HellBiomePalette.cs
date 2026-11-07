namespace VoxelEngine.WorldGen.Hell;

/// <summary>
/// The blocks one <see cref="HellBiome"/> dresses its cavern pockets in, and how much of its pool
/// rows hold lava. Every block is solid, so a lava cell beside any of them stays put.
/// </summary>
/// <param name="Surface">The floor cell right under open cavern, and the top cell of a dry pool.</param>
/// <param name="Soil">The floor cells under the surface, and the rest of a dry pool.</param>
/// <param name="Wall">A cell beside open cavern that is neither floor nor ceiling.</param>
/// <param name="Ceiling">The cells over open cavern.</param>
/// <param name="PoolBed">The floor under a lava pool.</param>
/// <param name="CeilingLight">The emissive block that replaces one ceiling cell right over open
/// cavern in <paramref name="CeilingLightRate"/>.</param>
/// <param name="CeilingLightRate">One ceiling cell in this many is <paramref name="CeilingLight"/>.</param>
/// <param name="FloorLight">The emissive block that replaces one surface cell in
/// <paramref name="FloorLightRate"/>, or air for none.</param>
/// <param name="FloorLightRate">One surface cell in this many is <paramref name="FloorLight"/>.</param>
/// <param name="LavaShare">The share of the pool columns that hold lava: 1 all, 0 none.</param>
public sealed record HellBiomePalette(
    byte Surface,
    byte Soil,
    byte Wall,
    byte Ceiling,
    byte PoolBed,
    byte CeilingLight,
    int CeilingLightRate,
    byte FloorLight,
    int FloorLightRate,
    float LavaShare);
