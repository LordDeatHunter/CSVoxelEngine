using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.WorldGen.Caves;

namespace VoxelEngine.WorldGen.Features.Underground;

/// <summary>
/// Tin ore veins: commoner than malachite, in a shallower and wider world-Y band
/// (<see cref="WorldGenerationConfig.TinMinY"/>..<see cref="WorldGenerationConfig.TinMaxY"/>),
/// the same <see cref="OreNoiseLattice"/> deposits on a noise of their own, stamped over stone
/// only. A pure function of world position like the other generators.
/// </summary>
public static class TinGenerator
{
    private static FastNoiseLite VeinNoise;

    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly ushort OreBlock;

    private static readonly ushort RockBlock;

    /// <summary>How the vein threshold shifts per cave biome (CB-2): richer in the frozen and desert echoes.</summary>
    internal static readonly CaveRichness Richness = new(
        (CaveBiome.FrozenEcho, WorldGenerationConfig.TinFrozenEchoShift),
        (CaveBiome.DesertEcho, WorldGenerationConfig.TinDesertEchoShift));

    static TinGenerator()
    {
        Reseed();
        OreBlock = ContentRegistry.RequireBlock("tin_ore").Id;
        RockBlock = ContentRegistry.RequireBlock("stone").Id;
    }

    /// <summary>Builds the noise from the current seed (static constructor and <see cref="WorldSeed.Apply"/>).</summary>
    internal static void Reseed() => VeinNoise = TerrainGenerator.MakeNoise(144, WorldGenerationConfig.TinFrequency);

    /// <summary>Whether the cell is ore in a plain cave biome cell (<see cref="HasOre(int, int, int, CaveBiome)"/>).</summary>
    public static bool HasOre(int x, int y, int z) => HasOre(x, y, z, CaveBiome.Plain);

    /// <summary>Whether the cell, of cave biome <paramref name="caveBiome"/>, is tin ore: inside the Y band and over
    /// the lattice threshold shifted by that biome's richness. Equals what <see cref="Stamp(ushort[], int, int, int, CaveBiomeMap)"/> writes given a map
    /// holding that biome there.</summary>
    public static bool HasOre(int x, int y, int z, CaveBiome caveBiome)
    {
        return y >= WorldGenerationConfig.TinMinY && y <= WorldGenerationConfig.TinMaxY
            && OreNoiseLattice.Exceeds(VeinNoise, x, y, z,
                WorldGenerationConfig.TinThreshold + Richness.ShiftOf(caveBiome));
    }

    /// <summary>Stamps the chunk as if every cell were plain (<see cref="Stamp(ushort[], int, int, int, CaveBiomeMap)"/>).</summary>
    public static void Stamp(ushort[] blocks, int worldX0, int worldY0, int worldZ0) =>
        Stamp(blocks, worldX0, worldY0, worldZ0, null);

    /// <summary>Replaces stone cells inside the band and a vein blob with ore; nothing else is touched.
    /// <paramref name="caves"/> is the chunk's cave biome map, null when every cell is plain.</summary>
    public static void Stamp(ushort[] blocks, int worldX0, int worldY0, int worldZ0, CaveBiomeMap? caves)
    {
        OreNoiseLattice.Stamp(blocks, VeinNoise, WorldGenerationConfig.TinThreshold,
            worldX0, worldY0, worldZ0,
            new VeinRule(WorldGenerationConfig.TinMinY - worldY0, WorldGenerationConfig.TinMaxY - worldY0, caves?.Cells));
    }

    /// <summary>Tin replaces stone only, inside the band (bounds as chunk-local Y), its threshold shifted by each
    /// cell's cave biome (null: all plain).</summary>
    private readonly struct VeinRule(int minLocalY, int maxLocalY, byte[]? biomes) : IOreLatticeRule
    {
        public bool IsHost(ushort block, int localX, int localY, int localZ)
            => localY >= minLocalY && localY <= maxLocalY && block == RockBlock;

        public ushort Replacement(int worldX, int worldY, int worldZ) => OreBlock;

        public float ThresholdShift(int localX, int localY, int localZ) =>
            biomes is null ? 0f : Richness.ShiftOf((CaveBiome)biomes[Chunk.Idx(localX, localY, localZ)]);

        public float MinThresholdShift => biomes is null ? 0f : Richness.MinShift;
    }
}
