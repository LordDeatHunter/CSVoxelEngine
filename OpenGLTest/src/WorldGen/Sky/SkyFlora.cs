using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Sky;

/// <summary>
/// The small flora and the sparkle of a sky island column (plans/LAYERS.md <c>L-13</c>), decided per
/// column from its own top and bottom alone, so it is part of <see cref="SkyIslandGenerator"/>'s one rule:
/// the point query and the stamp put the same blocks in the same cells, and a chunk reads no neighbor.
/// The top cell is the palette's <see cref="SkyIslandPalette.Sparkle"/> in one column in its rate; one plain
/// top cell in <see cref="SkyIslandPalette.PlantRate"/> holds a plant in the cell over it; and one underside
/// column in <see cref="SkyIslandPalette.VineRate"/> hangs a vine of 1 to
/// <see cref="SkyIslandConfig.SkyVineMaxLength"/> cells straight down from its bottom cell, so every vine
/// cell hangs from the island or the vine cell over it. Trees are separate (<see cref="SkyTrees"/>): they
/// grow over this and may replace a plant.
/// </summary>
internal static class SkyFlora
{
    // Hash salts, so the sparkle, the plants and the vines never pick the same columns
    private const ulong SparkleSalt = 0x534B5350UL;
    private const ulong PlantSalt = 0x534B504CUL;
    private const ulong PlantPickSalt = 0x534B5050UL;
    private const ulong VineSalt = 0x534B5649UL;
    private const ulong VineLengthSalt = 0x534B564CUL;

    /// <summary>The vine hanging under the islands.</summary>
    internal static readonly ushort VineBlock;

    static SkyFlora() => VineBlock = ContentRegistry.RequireBlock("sky_vine").Id;

    /// <summary>The block of island column (x, z), whose island cells run <paramref name="bottom"/>..<paramref name="top"/>,
    /// at world y: the island, its plant, its vine, or air.</summary>
    internal static ushort BlockAt(SkyIslandPalette palette, int x, int y, int z, int bottom, int top)
    {
        if (y >= bottom && y <= top)
            return y == top && IsSparkle(palette, x, z) ? palette.Sparkle : palette.BlockAt(y, top);
        if (y == top + 1) return PlantAt(palette, x, z);
        return y < bottom && y >= bottom - VineLength(palette, x, z) ? VineBlock : BlockType.Air;
    }

    /// <summary>Whether the top cell of column (x, z) is the palette's sparkle.</summary>
    internal static bool IsSparkle(SkyIslandPalette palette, int x, int z) =>
        palette.Sparkle != BlockType.Air && SkyBiomes.Hash(x, z, SparkleSalt) % (ulong)palette.SparkleRate == 0;

    /// <summary>The plant over the top cell of column (x, z), or air: never over a sparkle.</summary>
    internal static ushort PlantAt(SkyIslandPalette palette, int x, int z)
    {
        if (palette.Plants.Length == 0 || IsSparkle(palette, x, z)) return BlockType.Air;
        if (SkyBiomes.Hash(x, z, PlantSalt) % (ulong)palette.PlantRate != 0) return BlockType.Air;
        return palette.Plants[(int)(SkyBiomes.Hash(x, z, PlantPickSalt) % (ulong)palette.Plants.Length)];
    }

    /// <summary>How many cells the vine under column (x, z) is long: 0 for none.</summary>
    internal static int VineLength(SkyIslandPalette palette, int x, int z)
    {
        if (palette.VineRate <= 0 || SkyBiomes.Hash(x, z, VineSalt) % (ulong)palette.VineRate != 0) return 0;
        return 1 + (int)(SkyBiomes.Hash(x, z, VineLengthSalt) % SkyIslandConfig.SkyVineMaxLength);
    }
}
