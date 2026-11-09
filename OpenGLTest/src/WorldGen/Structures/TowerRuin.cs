using System;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Structures;

/// <summary>
/// What a conquered battle tower leaves behind once it collapses (BT-8): the pure ruin rule of one tower cell, GL-free.
/// <list type="bullet">
/// <item>each wall column (outer wall, stair corridor, inner wall) keeps a hashed stump
/// <see cref="WorldGenerationConfig.TowerRuinStumpMinBlocks"/>..<see cref="WorldGenerationConfig.TowerRuinStumpMaxBlocks"/>
/// blocks over the ground slab; everything above a stump falls to air;</item>
/// <item>every unbreakable brick at or below its stump (the ground slab and the foundation included) crumbles to stone bricks
/// or cobblestone, so nothing unbreakable survives; the doors, piles and the altar become air;</item>
/// <item>the ground floor's breakable dressing standing on the ground slab (its chests included) stays, and a few of its air
/// cells get a rubble pile of cobblestone or gravel, always resting on the solid rubble floor; every higher floor's dressing,
/// chests included, falls to air (the writer spills a replaced chest);</item>
/// <item>breakable cells below a stump (stair slabs) and the whole entrance apron are kept.</item>
/// </list>
/// The result depends only on the cell, the world seed and the site, so a chunk that loads late gets the same ruin; nothing is
/// cached.
/// </summary>
public static class TowerRuin
{
    private const int RuinSalt = 0x5E80;

    private static readonly byte StoneBricks;
    private static readonly byte Cobblestone;
    private static readonly byte Gravel;

    static TowerRuin()
    {
        StoneBricks = ContentRegistry.RequireBlock("stone_bricks").Id;
        Cobblestone = ContentRegistry.RequireBlock("cobblestone").Id;
        Gravel = ContentRegistry.RequireBlock("gravel").Id;
    }

    /// <summary>The highest world y a column in <paramref name="ring"/> keeps after the collapse: the ground slab for the rooms,
    /// a hashed stump for a wall column, everything for the apron.</summary>
    internal static int StumpTop(TowerSite s, TowerRing ring, int x, int z)
    {
        switch (ring)
        {
            case TowerRing.Room:
                return s.GroundY;
            case TowerRing.OuterWall or TowerRing.Corridor or TowerRing.InnerWall:
                const int min = WorldGenerationConfig.TowerRuinStumpMinBlocks;
                const int max = WorldGenerationConfig.TowerRuinStumpMaxBlocks;
                var height = Math.Min(max, min + (int)(SplitMix64.Unit(ColumnHash(x, z), 0) * (max - min + 1)));
                return Math.Min(s.RoofY, s.GroundY + height);
            default:
                return int.MaxValue;
        }
    }

    /// <summary>
    /// The ruin of the cell at world (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) of
    /// <paramref name="s"/>: -1 when the cell keeps what the tower wrote there (a cell the tower never writes included), else
    /// <c>block | state &lt;&lt; 8</c> (the state is always 0). <paramref name="original"/> is the tower's own cell
    /// (<see cref="TowerGenerator.CellAt(TowerSite, int, int, int)"/>), -1 where it writes nothing.
    /// </summary>
    public static int RuinAt(TowerSite s, int x, int y, int z, out int original)
    {
        var dx = x - s.CenterX;
        var dz = z - s.CenterZ;
        var step = TowerShell.StepOf(s, dx, dz);
        var ring = TowerShell.RingOf(s, dx, dz, step);
        original = ring == TowerRing.Outside ? -1 : TowerGenerator.CellIn(s, ring, step, x, y, z);
        if (original < 0) return -1;

        var block = (byte)original;
        var top = StumpTop(s, ring, x, z);
        if (y > top) return AboveStump(s, ring, block, x, y, z);
        if (!BlockType.IsUnbreakable(block)) return -1;
        if (block == TowerPalette.DoorBlock || block == TowerPalette.PileBlock || block == TowerPalette.AltarBlock)
            return BlockType.Air;
        return Rubble(x, y, z);
    }

    // A cell over its column's stump: air, but for the ground floor's breakable dressing and its rubble piles
    private static int AboveStump(TowerSite s, TowerRing ring, byte block, int x, int y, int z)
    {
        if (ring == TowerRing.Room && y == s.GroundY + 1)
        {
            if (block != BlockType.Air) return BlockType.IsUnbreakable(block) ? BlockType.Air : -1;
            var h = ColumnHash(x, z);
            if (SplitMix64.Unit(h, 1) >= WorldGenerationConfig.TowerRuinRubbleChance) return -1;
            return SplitMix64.Unit(h, 2) < WorldGenerationConfig.TowerRuinGravelShare ? Gravel : Cobblestone;
        }
        return block == BlockType.Air ? -1 : BlockType.Air;
    }

    // A crumbled brick: cobblestone or stone bricks, hashed per cell
    private static int Rubble(int x, int y, int z)
    {
        var hc = SplitMix64.Mix(ColumnHash(x, z) ^ (ulong)(uint)y);
        return SplitMix64.Unit(hc, 0) < WorldGenerationConfig.TowerRuinCobbleShare ? Cobblestone : StoneBricks;
    }

    private static ulong ColumnHash(int x, int z) => SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ RuinSalt);
}
