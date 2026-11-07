using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Hell;

/// <summary>
/// The hell pockets' small flora (plans/LAYERS.md L-9), decided per air cell from the open field
/// alone, so it is part of <see cref="HellGenerator"/>'s one rule: the point query and the stamp put
/// the same flora in the same cells, and a chunk reads nothing outside itself (the stamp's open mask
/// reaches <see cref="ReachUp"/> rows over the chunk for the vines). An air cell is a <b>vine</b>
/// when the first solid cell over it, at most <see cref="ReachUp"/> cells up, is a ceiling cell whose
/// hash hangs a vine at least that long: a ceiling cell hangs one in its biome's
/// <see cref="HellBiomePalette.VineRate"/>, 1 to <see cref="ReachUp"/> cells, stopping early at the
/// floor, so every vine cell hangs from a solid cell or the vine cell over it. Otherwise an air cell
/// over its biome's surface (a floor, or a dry pool's top) is now and then that biome's
/// <see cref="HellBiomePalette.GroundPlant"/>. Trees are separate (<see cref="HellTrees"/>): they
/// grow over this and may replace a plant or a vine segment, which leaves the rest held by the tree.
/// </summary>
internal static class HellFlora
{
    /// <summary>How far over an air cell the rule reads: the longest vine.</summary>
    internal const int ReachUp = WorldGenerationConfig.HellVineMaxLength;

    // Hash salts, so the vines, the plants and the ceiling lights never pick the same cells
    private const ulong VineSalt = 0x56494E45UL;
    private const ulong VineLengthSalt = 0x564C454EUL;
    private const ulong PlantSalt = 0x504C4E54UL;

    /// <summary>The hanging vine block.</summary>
    internal static readonly byte VineBlock;

    static HellFlora() => VineBlock = ContentRegistry.RequireBlock("hell_vine").Id;

    /// <summary>What air cell (x, y, z) (open cavern above its layer's lava level) holds: a vine, a
    /// ground plant, or air.</summary>
    internal static byte AirCell<TField>(ref TField field, int x, int y, int z)
        where TField : IHellField, allows ref struct
    {
        // The first closed cell over the air cell within a vine's reach, if any. Every cell between
        // them is air: open cells over air are never pool cells (the pool rows are a layer's lowest)
        var ceiling = 0;
        for (var k = 1; k <= ReachUp && ceiling == 0; k++)
            if (!field.IsOpen(x, y + k, z)) ceiling = k;

        // Most air cells hang in mid-cavern, over more air and under no ceiling in reach: they read
        // the open field only
        var overAir = field.IsOpen(x, y - 1, z) && !field.InPool(y - 1);
        if (ceiling == 0 && overAir) return BlockType.Air;

        var palette = HellBiomes.Palette(field.Biome(x, HellGenerator.LayerIndex(y), z));
        if (ceiling > 0 && palette.VineRate > 0 && ceiling <= VineLength(x, y + ceiling, z, palette.VineRate))
            return VineBlock;

        if (overAir || palette.GroundPlant == BlockType.Air || !StandsOnSurface(ref field, palette, x, y, z))
            return BlockType.Air;
        return HellBiomes.Hash(x, y, z, PlantSalt) % (ulong)palette.GroundPlantRate == 0 ? palette.GroundPlant : BlockType.Air;
    }

    /// <summary>How many cells the vine hanging from ceiling cell (x, y, z) is long: 0 for none, else
    /// 1 to <see cref="ReachUp"/>.</summary>
    internal static int VineLength(int x, int y, int z, int rate)
    {
        if (HellBiomes.Hash(x, y, z, VineSalt) % (ulong)rate != 0) return 0;
        return 1 + (int)(HellBiomes.Hash(x, y, z, VineLengthSalt) % ReachUp);
    }

    /// <summary>Whether the cell under air cell (x, y, z) is its biome's surface: a floor's top cell
    /// (not its floor light) or a dry pool's top cell.</summary>
    private static bool StandsOnSurface<TField>(ref TField field, HellBiomePalette palette, int x, int y, int z)
        where TField : IHellField, allows ref struct
    {
        var below = y - 1;
        if (!field.CanWearSkin(below)) return false;
        // Under an air cell an open cell is a pool row's top: dry it is the surface, else lava
        if (field.IsOpen(x, below, z)
            && (!field.InPool(below) || field.PoolIsLava(x, HellGenerator.LayerIndex(below), z))) return false;
        return HellGenerator.Surface(palette, x, below, z) == palette.Surface;
    }
}
