using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features.Underground;

/// <summary>
/// Hematite (iron) ore veins: sparse blobs where a low-frequency 3D OpenSimplex2 field of its
/// own, sampled on the world-aligned <see cref="OreNoiseLattice"/> and trilinearly interpolated,
/// exceeds its threshold, stamped over stone at or below
/// <see cref="WorldGenerationConfig.HematiteMaxY"/> only (the lava pockets' deep band), and
/// rarer than malachite. A pure function of world position like the other generators, so
/// neighbors agree without shared state. Runs on a generation worker thread after the carvers,
/// like <see cref="MalachiteGenerator"/>, so cave walls are rimmed with exposed ore.
/// </summary>
public static class HematiteGenerator
{
    private static FastNoiseLite VeinNoise;

    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte OreBlock;

    private static readonly byte RockBlock;

    static HematiteGenerator()
    {
        Reseed();
        OreBlock = ContentRegistry.RequireBlock("hematite_ore").Id;
        RockBlock = ContentRegistry.RequireBlock("stone").Id;
    }

    /// <summary>
    /// Builds the noise from the current <see cref="WorldGenerationConfig.Seed"/>: once from the
    /// static constructor, and again by <see cref="WorldSeed.Apply"/> while no worker thread runs.
    /// </summary>
    internal static void Reseed() => VeinNoise = TerrainGenerator.MakeNoise(143, WorldGenerationConfig.HematiteFrequency);

    /// <summary>
    /// Whether the stone cell (x, y, z) is hematite ore: at or below the
    /// <see cref="WorldGenerationConfig.HematiteMaxY"/> ceiling, the interpolated
    /// <see cref="OreNoiseLattice"/> field exceeds the threshold there. Threshold and ceiling test
    /// only; the caller decides what counts as a replaceable host. Equals what
    /// <see cref="Stamp"/> writes, the ceiling included.
    /// </summary>
    public static bool HasOre(int x, int y, int z)
    {
        return y <= WorldGenerationConfig.HematiteMaxY
            && OreNoiseLattice.Exceeds(VeinNoise, x, y, z, WorldGenerationConfig.HematiteThreshold);
    }

    /// <summary>
    /// Replaces stone cells at or below the ceiling inside a vein blob of the interpolated
    /// <see cref="OreNoiseLattice"/> field with ore. Only touches stone, so sandstone, soil,
    /// water, lava, air and every other feature's output pass through untouched.
    /// </summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        OreNoiseLattice.Stamp(blocks, VeinNoise, WorldGenerationConfig.HematiteThreshold,
            worldX0, worldY0, worldZ0, new VeinRule(WorldGenerationConfig.HematiteMaxY - worldY0));
    }

    /// <summary>
    /// Hematite replaces stone only, and only at or below the ceiling. <see cref="IsHost"/> sees
    /// chunk-local coordinates, so the ceiling is captured as a local Y (the world ceiling less
    /// the chunk's origin). A lattice cell wholly above the ceiling holds no host and is therefore
    /// never sampled; sandstone, soil, water, lava and air pass through at any depth.
    /// </summary>
    private readonly struct VeinRule(int maxLocalY) : IOreLatticeRule
    {
        public bool IsHost(byte block, int localX, int localY, int localZ) => localY <= maxLocalY && block == RockBlock;

        public byte Replacement(int worldX, int worldY, int worldZ) => OreBlock;
    }
}
