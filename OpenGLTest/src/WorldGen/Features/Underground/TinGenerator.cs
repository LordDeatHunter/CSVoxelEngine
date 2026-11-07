using VoxelEngine.Content;

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
    public static readonly byte OreBlock;

    private static readonly byte RockBlock;

    static TinGenerator()
    {
        Reseed();
        OreBlock = ContentRegistry.RequireBlock("tin_ore").Id;
        RockBlock = ContentRegistry.RequireBlock("stone").Id;
    }

    /// <summary>Builds the noise from the current seed (static constructor and <see cref="WorldSeed.Apply"/>).</summary>
    internal static void Reseed() => VeinNoise = TerrainGenerator.MakeNoise(144, WorldGenerationConfig.TinFrequency);

    /// <summary>Whether the cell is tin ore: inside the Y band and over the lattice threshold. Equals what <see cref="Stamp"/> writes.</summary>
    public static bool HasOre(int x, int y, int z)
    {
        return y >= WorldGenerationConfig.TinMinY && y <= WorldGenerationConfig.TinMaxY
            && OreNoiseLattice.Exceeds(VeinNoise, x, y, z, WorldGenerationConfig.TinThreshold);
    }

    /// <summary>Replaces stone cells inside the band and a vein blob with ore; nothing else is touched.</summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        OreNoiseLattice.Stamp(blocks, VeinNoise, WorldGenerationConfig.TinThreshold,
            worldX0, worldY0, worldZ0,
            new VeinRule(WorldGenerationConfig.TinMinY - worldY0, WorldGenerationConfig.TinMaxY - worldY0));
    }

    /// <summary>Tin replaces stone only, inside the band (bounds as chunk-local Y).</summary>
    private readonly struct VeinRule(int minLocalY, int maxLocalY) : IOreLatticeRule
    {
        public bool IsHost(byte block, int localX, int localY, int localZ)
            => localY >= minLocalY && localY <= maxLocalY && block == RockBlock;

        public byte Replacement(int worldX, int worldY, int worldZ) => OreBlock;
    }
}
