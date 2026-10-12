using silkgltriangle.Content;

namespace silkgltriangle;

/// <summary>
/// Malachite ore veins: sparse large blobs where a low-frequency 3D OpenSimplex2 field
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

    /// <summary>
    /// Whether the stone cell (x, y, z) is malachite ore. Threshold test only; the
    /// caller decides what counts as a replaceable host.
    /// </summary>
    public static bool HasOre(int x, int y, int z)
    {
        return VeinNoise.GetNoise(x, y, z) > WorldGenerationConfig.MalachiteThreshold;
    }

    /// <summary>
    /// Replaces stone cells inside a vein blob with ore. Only touches stone, so soil,
    /// sand, water, lava, air and every other feature's output pass through untouched.
    /// </summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        for (var y = 0; y < Chunk.Size; y++)
        {
            var i = Chunk.Idx(x, y, z);
            if (blocks[i] != RockBlock) continue;
            if (VeinNoise.GetNoise(worldX0 + x, worldY0 + y, worldZ0 + z)
                <= WorldGenerationConfig.MalachiteThreshold) continue;

            blocks[i] = OreBlock;
        }
    }
}
