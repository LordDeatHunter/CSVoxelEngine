using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.WorldGen.Caves;

namespace VoxelEngine.WorldGen.Features.Underground;

/// <summary>
/// Coal ore veins: the same threshold-on-3D-noise deposits as <see cref="MalachiteGenerator"/>
/// (the noise sampled on the world-aligned <see cref="OreNoiseLattice"/> and trilinearly interpolated),
/// but hostable in sandstone as well as stone (so a desert column's shallow band carries coal)
/// and slightly larger per deposit. A pure function of world position like the other generators,
/// so neighbors agree without shared state. Runs on a generation worker thread after the
/// carvers, for the same reason malachite does: carving first leaves cave walls rimmed with
/// exposed ore instead of eating half of every deposit the caves cross.
/// </summary>
public static class CoalGenerator
{
    private static FastNoiseLite VeinNoise;

    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte OreBlock;

    private static readonly byte RockBlock;
    private static readonly byte SandstoneBlock;

    /// <summary>How the vein threshold shifts per cave biome (CB-2): richer in fungal and desert echo caves,
    /// burned out in the magma-touched deep.</summary>
    internal static readonly CaveRichness Richness = new(
        (CaveBiome.Fungal, WorldGenerationConfig.CoalFungalShift),
        (CaveBiome.DesertEcho, WorldGenerationConfig.CoalDesertEchoShift),
        (CaveBiome.MagmaDeep, WorldGenerationConfig.CoalMagmaDeepShift));

    static CoalGenerator()
    {
        Reseed();
        OreBlock = ContentRegistry.RequireBlock("coal_ore").Id;
        RockBlock = ContentRegistry.RequireBlock("stone").Id;
        SandstoneBlock = ContentRegistry.RequireBlock("sandstone").Id;
    }

    /// <summary>
    /// Builds the noise from the current <see cref="WorldGenerationConfig.Seed"/>: once from the
    /// static constructor, and again by <see cref="WorldSeed.Apply"/> while no worker thread runs.
    /// </summary>
    internal static void Reseed() => VeinNoise = TerrainGenerator.MakeNoise(142, WorldGenerationConfig.CoalFrequency);

    /// <summary>Whether the cell is ore in a plain cave biome cell (<see cref="HasOre(int, int, int, CaveBiome)"/>).</summary>
    public static bool HasOre(int x, int y, int z) => HasOre(x, y, z, CaveBiome.Plain);

    /// <summary>
    /// Whether the host cell (x, y, z), of cave biome <paramref name="caveBiome"/>, is coal ore: the
    /// interpolated <see cref="OreNoiseLattice"/> field exceeds the threshold shifted by that biome's
    /// richness there. Threshold test only; the caller decides what counts as a replaceable host. Equals
    /// what <see cref="Stamp(byte[], int, int, int, CaveBiomeMap)"/> writes given a map holding that biome there.
    /// </summary>
    public static bool HasOre(int x, int y, int z, CaveBiome caveBiome)
    {
        return OreNoiseLattice.Exceeds(VeinNoise, x, y, z,
            WorldGenerationConfig.CoalThreshold + Richness.ShiftOf(caveBiome));
    }

    /// <summary>Stamps the chunk as if every cell were plain (<see cref="Stamp(byte[], int, int, int, CaveBiomeMap)"/>).</summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0) =>
        Stamp(blocks, worldX0, worldY0, worldZ0, null);

    /// <summary>
    /// Replaces stone and sandstone cells inside a vein blob of the interpolated
    /// <see cref="OreNoiseLattice"/> field with ore. Only those two hosts, so soil, sand, water,
    /// lava, air and every other feature's output pass through untouched. <paramref name="caves"/> is the
    /// chunk's cave biome map, null when every cell is plain.
    /// </summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0, CaveBiomeMap? caves)
    {
        OreNoiseLattice.Stamp(blocks, VeinNoise, WorldGenerationConfig.CoalThreshold,
            worldX0, worldY0, worldZ0, new VeinRule(caves?.Cells));
    }

    /// <summary>Coal replaces stone and sandstone, its threshold shifted by each cell's cave biome (null: all plain).</summary>
    private readonly struct VeinRule(byte[]? biomes) : IOreLatticeRule
    {
        public bool IsHost(byte block, int localX, int localY, int localZ) =>
            block == RockBlock || block == SandstoneBlock;

        public byte Replacement(int worldX, int worldY, int worldZ) => OreBlock;

        public float ThresholdShift(int localX, int localY, int localZ) =>
            biomes is null ? 0f : Richness.ShiftOf((CaveBiome)biomes[Chunk.Idx(localX, localY, localZ)]);

        public float MinThresholdShift => biomes is null ? 0f : Richness.MinShift;
    }
}
