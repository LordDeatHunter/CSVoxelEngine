namespace VoxelEngine.WorldGen.Caves;

/// <summary>
/// How a cave biome changes the carved shape (plans/CAVE_BIOMES.md CB-2): crystal opens bigger cheese rooms,
/// fungal widens the spaghetti tunnels and the worm nodes, the magma-touched deep does a little of both and
/// opens more caverns. Plain and frozen echo keep the regular fields exactly (a boost of 0f, a factor of 1f).
/// <see cref="NoiseCaves"/> scales the boosts per region by its shape channel; <see cref="WormCarver"/> reads
/// the radius factor at each node's center.
/// </summary>
internal static class CaveShape
{
    /// <summary>Whether <paramref name="biome"/> changes the shape at all (the sub-band biomes).</summary>
    public static bool Shapes(CaveBiome biome) =>
        biome is CaveBiome.Crystal or CaveBiome.Fungal or CaveBiome.MagmaDeep;

    /// <summary>How much <paramref name="biome"/> lowers the cheese room threshold.</summary>
    public static float CheeseBoost(CaveBiome biome) => biome switch
    {
        CaveBiome.Crystal => WorldGenerationConfig.CaveCrystalCheeseBoost,
        CaveBiome.MagmaDeep => WorldGenerationConfig.CaveMagmaCheeseBoost,
        _ => 0f,
    };

    /// <summary>The share <paramref name="biome"/> widens the spaghetti tunnel radii by.</summary>
    public static float TunnelBoost(CaveBiome biome) =>
        biome == CaveBiome.Fungal ? WorldGenerationConfig.CaveFungalTunnelBoost : 0f;

    /// <summary>How much <paramref name="biome"/> lowers the cavern threshold.</summary>
    public static float CavernBoost(CaveBiome biome) =>
        biome == CaveBiome.MagmaDeep ? WorldGenerationConfig.CaveMagmaCavernBoost : 0f;

    /// <summary>What a worm node centered in a <paramref name="biome"/> cell has its radius multiplied by.</summary>
    public static float WormRadiusFactor(CaveBiome biome) => biome switch
    {
        CaveBiome.Fungal => WorldGenerationConfig.CaveFungalWormRadiusFactor,
        CaveBiome.MagmaDeep => WorldGenerationConfig.CaveMagmaWormRadiusFactor,
        _ => 1f,
    };
}
