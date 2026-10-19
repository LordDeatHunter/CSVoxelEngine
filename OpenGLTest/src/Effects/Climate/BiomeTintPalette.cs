using VoxelEngine.WorldGen;

namespace VoxelEngine.Effects.Climate;

/// <summary>
/// Which climate palette a biome-tinted block wears (<see cref="BiomeTint"/>). Both palettes are
/// keyed to the same <see cref="TerrainGenerator.Climate"/> axes.
/// </summary>
public enum BiomeTintPalette : byte
{
    /// <summary>The grass top and the grass band of its sides
    /// (<see cref="SurfaceShadingConfig.GrassColorWarmWet"/> and its siblings).</summary>
    Grass,

    /// <summary>Leaves, a little darker than the grass
    /// (<see cref="SurfaceShadingConfig.FoliageColorWarmWet"/> and its siblings).</summary>
    Foliage,
}
