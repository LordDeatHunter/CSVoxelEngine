namespace VoxelEngine.WorldGen;

/// <summary>
/// Shared yes/no traits of a <see cref="Biome"/>, so the near chunk fill (<see cref="TerrainGenerator.Compute"/> and
/// <c>ColumnCache.GenerateColumn</c>) and both far-LOD column paths (<c>LodMeshBuilder.ClassifyGenerated</c> for a
/// generated column, <c>LodMeshBuilder.ClassifySupplied</c> for a world-source column) ask one rule instead of each
/// listing biomes of its own.
/// </summary>
internal static class BiomeTraits
{
    /// <summary>The snowless grass biomes whose stripped columns (a peak above
    /// <see cref="WorldGenerationConfig.MountainRockHeight"/>, a deep ravine) show bare stone on top.</summary>
    public static bool ShowsBareRock(Biome biome) => biome is Biome.Forest or Biome.Steppe or Biome.Savanna or Biome.Jungle;

    /// <summary>The grass-fill biomes (the column fill's default branch) whose lake and river rims wear
    /// beach sand.</summary>
    public static bool RimsWaterWithSand(Biome biome) =>
        biome is Biome.Forest or Biome.Taiga or Biome.Steppe or Biome.Savanna or Biome.Jungle;
}
