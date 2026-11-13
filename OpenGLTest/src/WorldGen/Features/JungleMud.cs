using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// The jungle's mud: wet patches laid over the rainforest floor where a low-frequency noise (seed offset
/// 188) is high. A pure function of (x, z) and the seed, read by the column fill
/// (<c>ColumnCache.GenerateColumn</c>) for jungle columns only, so no other biome ever wears it.
/// </summary>
public static class JungleMud
{
    private static FastNoiseLite PatchNoise;

    /// <summary>The mud block, resolved once in the static constructor.</summary>
    public static readonly ushort MudBlock;

    static JungleMud()
    {
        Reseed();
        MudBlock = ContentRegistry.RequireBlock("jungle_mud").Id;
    }

    /// <summary>Builds the noise from the current <see cref="WorldGenerationConfig.Seed"/>: once from the
    /// static constructor, and again by <see cref="WorldSeed.Apply"/> while no worker thread runs.</summary>
    internal static void Reseed() => PatchNoise = TerrainGenerator.MakeNoise(188, WorldFeatureConfig.JungleMudFrequency);

    /// <summary>Whether the jungle surface column (x, z) lies inside a mud patch.</summary>
    public static bool IsMud(int x, int z) =>
        PatchNoise.GetNoise(x, z) > WorldFeatureConfig.JungleMudThreshold;
}