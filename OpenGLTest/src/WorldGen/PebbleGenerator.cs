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
    private const int ObsidianSalt = 0x6E8B25;

    // True texels per block edge: block textures are 32x32 (see TextureAtlas).
    private const int TexelsPerBlock = 32;

    private static readonly FastNoiseLite PatchNoise;

    /// <summary>Block this generator stamps; resolved once, like the noise.</summary>
    public static readonly byte PebbleBlock;

    /// <summary>Obsidian block whose tile tints obsidian stones; resolved once.</summary>
    public static readonly byte ObsidianBlock;

    /// <summary>Obsidian shard item that grounds and grows obsidian-bearing piles.</summary>
    public static readonly byte ObsidianItem;

    static PebbleGenerator()
    {
        PatchNoise = TerrainGenerator.MakeNoise(61, WorldGenerationConfig.PebblePatchFrequency);
        PebbleBlock = ContentRegistry.RequireBlock("pebble").Id;
        ObsidianBlock = ContentRegistry.RequireBlock("obsidian").Id;
        ObsidianItem = ContentRegistry.RequireItem("obsidian").Id;
    }

    /// <summary>
    /// Whether the surface column (x, z) grows pebbles, and how many stones share its cell.
    /// Rare low-frequency patches gate, then a density roll thins inside them;
    /// <paramref name="surfaceWeight"/> (the exposed surface's
    /// <see cref="BlockType.PebbleWeight"/>) scales that roll, so rock and gravel keep the
    /// patch's full density, grass gets a thinner scatter and sand none.
    /// </summary>
    public static bool TryGetPebble(int x, int z, float surfaceWeight, out int count)
    {
        count = 0;
        if (surfaceWeight <= 0f) return false;
        if (PatchNoise.GetNoise(x, z) <= WorldGenerationConfig.PebblePatchThreshold) return false;
        if (UnitHash(x, z, DensitySalt) >= WorldGenerationConfig.PebbleDensity * surfaceWeight) return false;
        count = CountAt(x, z);
        return true;
    }

    /// <summary>Visible stones in the cell at (x, z): 1-3. Also the flint yield on mining.</summary>
    public static int CountAt(int x, int z) => 1 + (int)(UnitHash(x, z, CountSalt) * 3f);

    /// <summary>
    /// Stones rendered for a pebble cell carrying this state byte: an explicit player-built
    /// pile reads back its packed total, while 0 — untouched worldgen, which never writes
    /// state — keeps the position-hash default above.
    /// </summary>
    public static int CountWithState(int x, int z, byte state, int max) =>
        TryDecode(state, max, out var total, out _) ? total : CountAt(x, z);

    /// <summary>
    /// Obsidian stones in the cell: the packed count for a player pile, or the ~1% hash roll
    /// per stone for untouched worldgen. Flint is always the total minus this.
    /// </summary>
    public static int ObsidianCountWithState(int x, int z, byte state, int max)
    {
        if (TryDecode(state, max, out var total, out var obsidian))
            return obsidian;
        var count = 0;
        var natural = CountAt(x, z);
        for (var i = 0; i < natural; i++)
            if (IsObsidianAt(x, z, i)) count++;
        return count;
    }

    /// <summary>Whether this stone of a natural pile is obsidian (~1% per rock).</summary>
    public static bool IsObsidianAt(int x, int z, int i) =>
        UnitHash(x, z, ObsidianSalt, i) < WorldGenerationConfig.PebbleObsidianChance;

    /// <summary>Whether the held item builds obsidian-bearing piles.</summary>
    public static bool IsObsidianItem(byte itemId) => itemId == ObsidianItem;

    /// <summary>Packs a player pile: total stones with that many obsidian among them.</summary>
    public static byte StateFor(int total, int obsidian) => (byte)(total + (obsidian << 2));

    /// <summary>Unpacks a player-pile state; false for worldgen 0 and malformed values.</summary>
    public static bool TryDecode(byte state, int max, out int total, out int obsidian)
    {
        total = state & 3;
        obsidian = state >> 2;
        return total >= 1 && total <= max && obsidian >= 0 && obsidian <= total;
    }

    /// <summary>Layout rotation (0-3 quarter turns) so same-count neighbours don't look identical.</summary>
    public static int VariantAt(int x, int z) => (int)(UnitHash(x, z, VariantSalt) * 4f);

    /// <summary>
    /// Stamps pebbles onto the surface: one block above the heightmap, into air only, and only
    /// over an exposed surface whose <see cref="BlockType.PebbleWeight"/> admits them (a carve
    /// below leaves the cell empty instead of floating). Runs on a generation worker thread,
    /// after the trees.
    /// </summary>
    public static void Stamp(byte[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var height = column.Heights[(x << Chunk.SizeShift) | z];
            // Wet columns stay bare: the cell above a sea, lake or river bed is water, not air
            if (TerrainGenerator.IsWaterColumn(worldX0 + x, worldZ0 + z, height)) continue;
            var ly = height + 1 - worldY0;
            if ((uint)ly >= Chunk.Size) continue;
            // ly == 0 puts the ground in the chunk below, out of reach of a pure per-chunk
            // stamp; skip it like FlowerGenerator rather than guess the surface material
            if (ly == 0) continue;

            var i = Chunk.Idx(x, ly, z);
            if (blocks[i] != BlockType.Air) continue;
            var weight = BlockType.PebbleWeightOf(blocks[Chunk.Idx(x, ly - 1, z)]);
            if (!TryGetPebble(worldX0 + x, worldZ0 + z, weight, out _)) continue;
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
