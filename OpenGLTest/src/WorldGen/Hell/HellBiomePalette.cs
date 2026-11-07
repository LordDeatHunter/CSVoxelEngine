using VoxelEngine.WorldGen.Trees;

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
/// <param name="GroundPlant">The plant one air cell over the biome's surface in
/// <paramref name="GroundPlantRate"/> holds (<see cref="HellFlora"/>), or air for none.</param>
/// <param name="GroundPlantRate">One air cell over the surface in this many grows <paramref name="GroundPlant"/>.</param>
/// <param name="VineRate">One ceiling cell over open cavern in this many hangs a vine; 0 for none.</param>
/// <param name="Tree">The tree species the biome's tree cells root (<see cref="HellTrees"/>), or null.</param>
/// <param name="TreeChance">The chance one tree cell roots <paramref name="Tree"/>.</param>
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
    float LavaShare,
    byte GroundPlant,
    int GroundPlantRate,
    int VineRate,
    TreeSpecies? Tree,
    float TreeChance);
