namespace silkgltriangle;

/// <summary>
/// Coal ore veins: the same threshold-on-3D-noise deposits as <see cref="MalachiteGenerator"/>,
/// but hostable in sandstone as well as stone (so a desert column's shallow band carries coal)
/// and slightly larger per deposit. A pure function of world position like the other generators,
/// so neighbours agree without shared state. Runs on a generation worker thread after the
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

    /// <summary>
    /// Whether the host cell (x, y, z) is coal ore. Threshold test only; the
    /// caller decides what counts as a replaceable host.
    /// </summary>
    public static bool HasOre(int x, int y, int z)
    {
        return VeinNoise.GetNoise(x, y, z) > WorldGenerationConfig.CoalThreshold;
    }

    /// <summary>
    /// Replaces stone and sandstone cells inside a vein blob with ore. Only those two hosts, so
    /// soil, sand, water, lava, air and every other feature's output pass through untouched.
    /// </summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        for (var y = 0; y < Chunk.Size; y++)
        {
            var i = Chunk.Idx(x, y, z);
            var host = blocks[i];
            if (host != RockBlock && host != SandstoneBlock) continue;
            if (VeinNoise.GetNoise(worldX0 + x, worldY0 + y, worldZ0 + z)
                <= WorldGenerationConfig.CoalThreshold) continue;

            blocks[i] = OreBlock;
        }
    }
}
