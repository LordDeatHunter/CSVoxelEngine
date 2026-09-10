using System;

namespace silkgltriangle;

/// <summary>
/// Ankle-high stones in rare patches: 1-3 small boxes sharing one air cell above the surface,
/// each 4-8 texels in size and randomly rotated about Y. A pure function of (x, z) like
/// TreeGenerator, so chunks agree without shared state. The visible count doubles as the
/// flint yield, recomputed from the same hash at break time, so no per-cell storage is needed.
/// </summary>
public static class PebbleGenerator
{
    private const int PatchSalt = 0x6E8B1F;
    private const int DensitySalt = 0x6E8B20;
    private const int CountSalt = 0x6E8B21;
    private const int VariantSalt = 0x6E8B22;
    private const int SizeSalt = 0x6E8B23;
    private const int AngleSalt = 0x6E8B24;

    // True texels per block edge: block textures are 32x32 (see TextureAtlas).
    private const int TexelsPerBlock = 32;

    private static readonly FastNoiseLite PatchNoise;

    /// <summary>Block this generator stamps; resolved once, like the noise.</summary>
    public static readonly byte PebbleBlock;

    static PebbleGenerator()
    {
        PatchNoise = TerrainGenerator.MakeNoise(61, WorldGenerationConfig.PebblePatchFrequency);
        PebbleBlock = ContentRegistry.RequireBlock("pebble").Id;
    }

    /// <summary>
    /// Whether the surface column (x, z) grows pebbles, and how many stones share its cell.
    /// Rare low-frequency patches gate, then a density roll thins inside them.
    /// </summary>
    public static bool TryGetPebble(int x, int z, out int count)
    {
        count = 0;
        if (PatchNoise.GetNoise(x, z) <= WorldGenerationConfig.PebblePatchThreshold) return false;
        if (UnitHash(x, z, DensitySalt) >= WorldGenerationConfig.PebbleDensity) return false;
        count = CountAt(x, z);
        return true;
    }

    /// <summary>Visible stones in the cell at (x, z): 1-3. Also the flint yield on mining.</summary>
    public static int CountAt(int x, int z) => 1 + (int)(UnitHash(x, z, CountSalt) * 3f);

    /// <summary>
    /// Stones rendered (and flints dropped) for a pebble cell carrying this state byte: an
    /// explicit player-built pile (1..<paramref name="max"/>) reads back directly, while 0 —
    /// untouched worldgen, which never writes state — keeps the position-hash default above.
    /// </summary>
    public static int CountWithState(int x, int z, byte state, int max) =>
        state >= 1 && state <= max ? state : CountAt(x, z);

    /// <summary>Layout rotation (0-3 quarter turns) so same-count neighbours don't look identical.</summary>
    public static int VariantAt(int x, int z) => (int)(UnitHash(x, z, VariantSalt) * 4f);

    /// <summary>
    /// Stamps pebbles onto the surface: one block above the heightmap, into air only, and only
    /// over solid ground inside this chunk (a carve below leaves the cell empty instead of
    /// floating). Runs on a generation worker thread, after the trees.
    /// </summary>
    public static void Stamp(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var height = column.Heights[(x << Chunk.SizeShift) | z];
            // Submerged columns stay bare: the cell above a sea floor is water, not air
            if (TerrainGenerator.IsSubmerged(height)) continue;
            var ly = height + 1 - worldY0;
            if ((uint)ly >= Chunk.Size) continue;
            if (!TryGetPebble(worldX0 + x, worldZ0 + z, out _)) continue;

            var i = Chunk.Idx(x, ly, z);
            if (blocks[i] != BlockType.Air) continue;
            if (ly > 0 && blocks[Chunk.Idx(x, ly - 1, z)] == BlockType.Air) continue;
            blocks[i] = PebbleBlock;
        }
    }

    /// <summary>
    /// Cell-local centre of one stone's slot. Base layouts are defined once, then rotated by
    /// the variant; the stone itself is a SizeAt cube on that centre, spun by AngleAt.
    /// </summary>
    public static void GetCenter(int count, int variant, int i, out float cx, out float cz)
    {
        (cx, cz) = (count, i) switch
        {
            (1, _) => (0.5f, 0.5f),
            (2, 0) => (0.27f, 0.27f),
            (2, _) => (0.73f, 0.73f),
            (3, 0) => (0.22f, 0.71f),
            (3, 1) => (0.725f, 0.725f),
            _ => (0.46f, 0.24f),
        };

        for (var r = 0; r < (variant & 3); r++)
            (cx, cz) = (1f - cz, cx);
    }

    /// <summary>Edge length of one stone, in blocks: an integer 4-8 true texels.</summary>
    public static float SizeAt(int x, int z, int i)
    {
        var range = WorldGenerationConfig.PebbleMaxSizeTexels - WorldGenerationConfig.PebbleMinSizeTexels + 1;
        return (WorldGenerationConfig.PebbleMinSizeTexels + (int)(UnitHash(x, z, SizeSalt, i) * range)) / (float)TexelsPerBlock;
    }

    /// <summary>Spin of one stone about Y, in radians.</summary>
    public static float AngleAt(int x, int z, int i) => UnitHash(x, z, AngleSalt, i) * MathF.Tau;

    private static float UnitHash(int x, int z, int salt, int i = 0)
    {
        var h = SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ (salt + i * 0x9E37));
        return (SplitMix64.Mix(h + 0x9E3779B97F4A7C15UL) >> 40) * (1f / (1 << 24));
    }
}
