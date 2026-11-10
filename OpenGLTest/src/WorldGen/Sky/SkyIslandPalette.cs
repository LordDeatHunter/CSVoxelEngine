using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.WorldGen.Sky;

/// <summary>
/// What one <see cref="SkyBiome"/> builds its islands of (<see cref="SkyBiomes.Palette"/>): the
/// <see cref="Top"/> cell, <see cref="SoilDepth"/> cells of <see cref="Soil"/> under it and a
/// <see cref="Core"/> below, plus the flora <see cref="SkyIslandGenerator"/> and <see cref="SkyTrees"/> grow
/// on it. <see cref="SkyIslandGenerator"/> reads it in one place per island, so the shape and the stamp
/// know nothing of the biomes.
/// </summary>
/// <param name="Top">The top cell of a column.</param>
/// <param name="Soil">The cells under the top.</param>
/// <param name="Core">Everything under the soil: the underside and its stalactites.</param>
/// <param name="SoilDepth">How many soil cells lie under the top.</param>
/// <param name="Sparkle">The block that replaces one top cell in <paramref name="SparkleRate"/>, or air for none.</param>
/// <param name="SparkleRate">One top cell in this many is <paramref name="Sparkle"/>.</param>
/// <param name="Plants">The plants one cell over a top cell in <paramref name="PlantRate"/> holds, picked by a
/// hash; empty for none.</param>
/// <param name="PlantRate">One top cell in this many grows a plant.</param>
/// <param name="VineRate">One underside column in this many hangs a vine; 0 for none.</param>
/// <param name="Tree">The tree species the biome's tree cells root, or null.</param>
/// <param name="TreeChance">The chance one tree cell roots <paramref name="Tree"/>.</param>
public sealed record SkyIslandPalette(
    ushort Top,
    ushort Soil,
    ushort Core,
    int SoilDepth,
    ushort Sparkle,
    int SparkleRate,
    ushort[] Plants,
    int PlantRate,
    int VineRate,
    TreeSpecies? Tree,
    float TreeChance)
{
    /// <summary>The block at world y of a column whose top cell is <paramref name="top"/>, before any sparkle.</summary>
    public ushort BlockAt(int y, int top) => y == top ? Top : y >= top - SoilDepth ? Soil : Core;
}
