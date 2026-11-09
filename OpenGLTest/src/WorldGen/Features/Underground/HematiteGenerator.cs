using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.WorldGen.Caves;

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

    /// <summary>How the vein threshold shifts per cave biome (CB-2): richer in the magma-touched deep.</summary>
    internal static readonly CaveRichness Richness = new((CaveBiome.MagmaDeep, WorldGenerationConfig.HematiteMagmaDeepShift));

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

    /// <summary>Whether the cell is ore in a plain cave biome cell (<see cref="HasOre(int, int, int, CaveBiome)"/>).</summary>
    public static bool HasOre(int x, int y, int z) => HasOre(x, y, z, CaveBiome.Plain);

    /// <summary>
    /// Whether the stone cell (x, y, z), of cave biome <paramref name="caveBiome"/>, is hematite ore: at
    /// or below the <see cref="WorldGenerationConfig.HematiteMaxY"/> ceiling, the interpolated
    /// <see cref="OreNoiseLattice"/> field exceeds the threshold shifted by that biome's richness there.
    /// Threshold and ceiling test only; the caller decides what counts as a replaceable host. Equals what
    /// <see cref="Stamp(byte[], int, int, int, CaveBiomeMap)"/> writes given a map holding that biome there, the ceiling included.
    /// </summary>
    public static bool HasOre(int x, int y, int z, CaveBiome caveBiome)
    {
        return y <= WorldGenerationConfig.HematiteMaxY
            && OreNoiseLattice.Exceeds(VeinNoise, x, y, z,
                WorldGenerationConfig.HematiteThreshold + Richness.ShiftOf(caveBiome));
    }

    /// <summary>Stamps the chunk as if every cell were plain (<see cref="Stamp(byte[], int, int, int, CaveBiomeMap)"/>).</summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0) =>
        Stamp(blocks, worldX0, worldY0, worldZ0, null);

    /// <summary>
    /// Replaces stone cells at or below the ceiling inside a vein blob of the interpolated
    /// <see cref="OreNoiseLattice"/> field with ore. Only touches stone, so sandstone, soil,
    /// water, lava, air and every other feature's output pass through untouched. <paramref name="caves"/>
    /// is the chunk's cave biome map, null when every cell is plain.
    /// </summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0, CaveBiomeMap? caves)
    {
        OreNoiseLattice.Stamp(blocks, VeinNoise, WorldGenerationConfig.HematiteThreshold,
            worldX0, worldY0, worldZ0, new VeinRule(WorldGenerationConfig.HematiteMaxY - worldY0, caves?.Cells));
    }

    /// <summary>
    /// Hematite replaces stone only, and only at or below the ceiling. <see cref="IsHost"/> sees
    /// chunk-local coordinates, so the ceiling is captured as a local Y (the world ceiling less
    /// the chunk's origin). A lattice cell wholly above the ceiling holds no host and is therefore
    /// never sampled; sandstone, soil, water, lava and air pass through at any depth. The threshold is
    /// shifted by each cell's cave biome (null: all plain).
    /// </summary>
    private readonly struct VeinRule(int maxLocalY, byte[]? biomes) : IOreLatticeRule
    {
        public bool IsHost(byte block, int localX, int localY, int localZ) => localY <= maxLocalY && block == RockBlock;

        public byte Replacement(int worldX, int worldY, int worldZ) => OreBlock;

        public float ThresholdShift(int localX, int localY, int localZ) =>
            biomes is null ? 0f : Richness.ShiftOf((CaveBiome)biomes[Chunk.Idx(localX, localY, localZ)]);

        public float MinThresholdShift => biomes is null ? 0f : Richness.MinShift;
    }
}
