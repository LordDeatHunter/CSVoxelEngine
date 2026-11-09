using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.WorldGen.Caves;

namespace VoxelEngine.WorldGen.Features.Underground;

/// <summary>
/// Malachite ore veins: sparse large blobs where a low-frequency 3D OpenSimplex2 field,
/// sampled on the world-aligned <see cref="OreNoiseLattice"/> and trilinearly interpolated,
/// exceeds its threshold, stamped over stone at any depth. A pure function of world
/// position like the other generators, so neighbors agree without shared state. Runs
/// on a generation worker thread after the carvers: carving first leaves cave walls
/// rimmed with exposed ore instead of eating half of every deposit the caves cross.
/// </summary>
public static class MalachiteGenerator
{
    private static FastNoiseLite VeinNoise;

    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly byte OreBlock;

    private static readonly byte RockBlock;

    /// <summary>How the vein threshold shifts per cave biome (CB-2): richer in crystal caves.</summary>
    internal static readonly CaveRichness Richness = new((CaveBiome.Crystal, WorldGenerationConfig.MalachiteCrystalShift));

    static MalachiteGenerator()
    {
        Reseed();
        OreBlock = ContentRegistry.RequireBlock("malachite_ore").Id;
        RockBlock = ContentRegistry.RequireBlock("stone").Id;
    }

    /// <summary>
    /// Builds the noise from the current <see cref="WorldGenerationConfig.Seed"/>: once from the
    /// static constructor, and again by <see cref="WorldSeed.Apply"/> while no worker thread runs.
    /// </summary>
    internal static void Reseed() => VeinNoise = TerrainGenerator.MakeNoise(141, WorldGenerationConfig.MalachiteFrequency);

    /// <summary>Whether the cell is ore in a plain cave biome cell (<see cref="HasOre(int, int, int, CaveBiome)"/>).</summary>
    public static bool HasOre(int x, int y, int z) => HasOre(x, y, z, CaveBiome.Plain);

    /// <summary>
    /// Whether the stone cell (x, y, z), of cave biome <paramref name="caveBiome"/>, is malachite ore: the
    /// interpolated <see cref="OreNoiseLattice"/> field exceeds the threshold shifted by that biome's
    /// richness there. Threshold test only; the caller decides what counts as a replaceable host. Equals
    /// what <see cref="Stamp(byte[], int, int, int, CaveBiomeMap)"/> writes given a map holding that biome there.
    /// </summary>
    public static bool HasOre(int x, int y, int z, CaveBiome caveBiome)
    {
        return OreNoiseLattice.Exceeds(VeinNoise, x, y, z,
            WorldGenerationConfig.MalachiteThreshold + Richness.ShiftOf(caveBiome));
    }

    /// <summary>Stamps the chunk as if every cell were plain (<see cref="Stamp(byte[], int, int, int, CaveBiomeMap)"/>).</summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0) =>
        Stamp(blocks, worldX0, worldY0, worldZ0, null);

    /// <summary>
    /// Replaces stone cells inside a vein blob of the interpolated <see cref="OreNoiseLattice"/>
    /// field with ore. Only touches stone, so soil, sand, water, lava, air and every other
    /// feature's output pass through untouched. <paramref name="caves"/> is the chunk's cave biome map,
    /// null when every cell is plain.
    /// </summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0, CaveBiomeMap? caves)
    {
        OreNoiseLattice.Stamp(blocks, VeinNoise, WorldGenerationConfig.MalachiteThreshold,
            worldX0, worldY0, worldZ0, new VeinRule(caves?.Cells));
    }

    /// <summary>Malachite replaces stone only, its threshold shifted by each cell's cave biome (null: all plain).</summary>
    private readonly struct VeinRule(byte[]? biomes) : IOreLatticeRule
    {
        public bool IsHost(byte block, int localX, int localY, int localZ) => block == RockBlock;

        public byte Replacement(int worldX, int worldY, int worldZ) => OreBlock;

        public float ThresholdShift(int localX, int localY, int localZ) =>
            biomes is null ? 0f : Richness.ShiftOf((CaveBiome)biomes[Chunk.Idx(localX, localY, localZ)]);

        public float MinThresholdShift => biomes is null ? 0f : Richness.MinShift;
    }
}
