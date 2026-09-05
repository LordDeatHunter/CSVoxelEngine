using System.Collections.Generic;

namespace silkgltriangle;

public class BlockType
{
    // Ids of the built-in blocks, for code that decides what to generate
    public const byte Air = 0;
    public const byte Grass = 1;
    public const byte Dirt = 2;
    public const byte Stone = 3;
    public const byte Cobblestone = 4;
    public const byte Tnt = 5;
    public const byte Lamp = 6;
    public const byte Log = 7;
    public const byte Branch = 8;
    public const byte Twig = 9;
    public const byte Leaves = 10;
    public const byte Limb = 11;
    public const byte Sprig = 12;
    public const byte Switch = 13;

    public static readonly Dictionary<byte, BlockType> Types = new()
    {
        { 1, new BlockType(new Point(0, 0), new Point(2, 0), new Point(1, 0)) }, // grass
        { 2, new BlockType(new Point(2, 0), new Point(2, 0), new Point(2, 0)) }, // dirt
        { 3, new BlockType(new Point(0, 1), new Point(0, 1), new Point(0, 1)) }, // stone
        { 4, new BlockType(new Point(3, 0), new Point(3, 0), new Point(3, 0)) }, // cobblestone
        { 5, new BlockType(new Point(2, 1), new Point(2, 1), new Point(1, 1)) }, // tnt
        { 6, new BlockType(new Point(3, 1), new Point(3, 1), new Point(3, 1)) }, // lamp
        { 7, new BlockType(new Point(1, 2), new Point(1, 2), new Point(0, 2)) }, // log (full-cube trunk base)
        { 8, new BlockType(new Point(1, 2), new Point(1, 2), new Point(0, 2)) }, // branch (taper level 1)
        { 9, new BlockType(new Point(1, 2), new Point(1, 2), new Point(0, 2)) }, // twig (taper level 5, tips)
        { 10, new BlockType(new Point(2, 2), new Point(2, 2), new Point(2, 2), maxHealth: 2) }, // leaves (cutout)
        { 11, new BlockType(new Point(1, 2), new Point(1, 2), new Point(0, 2)) }, // limb (taper level 2)
        { 12, new BlockType(new Point(1, 2), new Point(1, 2), new Point(0, 2)) }, // sprig (taper level 3)
        { 13, new BlockType(new Point(1, 2), new Point(1, 2), new Point(0, 2)) }, // switch (taper level 4)
    };

    // Array-indexed view of Types for the meshing hot path (no dictionary hashing per block)
    public static readonly BlockType?[] ById = BuildById();

    /// <summary>
    /// Light emitted by each block id (0-15), indexed like <see cref="ById"/>. Only the lamp
    /// glows; everything else is lit by the sun or by flood-filled lamp light.
    /// </summary>
    public static readonly byte[] Emission = BuildEmission();

    // Hits a block takes before it breaks; one hit lands per World.BlockHitSeconds of mining
    public const int DefaultMaxHealth = 5;

    public Point Top, Bottom, Side;

    // Tile per face, indexed in Chunk.CubeFaces order: -Z, +Z, -X, +X, +Y, -Y
    public readonly Point[] FaceTiles;

    public readonly int MaxHealth;

    public BlockType(Point top, Point bottom, Point side, int maxHealth = DefaultMaxHealth)
    {
        Top = top;
        Bottom = bottom;
        Side = side;
        MaxHealth = maxHealth;
        FaceTiles = [side, side, side, side, top, bottom];
    }

    private static BlockType?[] BuildById()
    {
        var byId = new BlockType?[256];
        foreach (var (id, type) in Types)
            byId[id] = type;
        return byId;
    }

    private static byte[] BuildEmission()
    {
        var emission = new byte[256];
        emission[Lamp] = 15;
        return emission;
    }

    /// <summary>Whether a block renders as a sub-cell tapered box instead of a full cube:
    /// every wood id except the full-cube log. Partial neighbours never hide a full cube's
    /// faces, so the ground still draws under a trunk and a box sitting on terrain never
    /// leaves a gap ring.</summary>
    public static bool IsPartialCube(byte blockId) =>
        blockId == Branch || blockId == Limb || blockId == Sprig || blockId == Switch || blockId == Twig;

    /// <summary>Half-width of a partial block's tapered box, in blocks. Full cubes are 0.5.
    /// Levels run Branch (0.75) down to Twig (0.25); see TreeGenerator for how limbs step
    /// through them.</summary>
    public static float PartialHalfWidth(byte blockId) => blockId switch
    {
        Branch => 6f / 16f,
        Limb => 5f / 16f,
        Sprig => 4f / 16f,
        Switch => 3f / 16f,
        Twig => 2f / 16f,
        _ => 0.5f,
    };
    /// <summary>Block id dropped when a block is mined; stone drops cobblestone, every tapered
    /// wood id drops logs, TNT drops nothing (mining it primes it instead), leaves drop
    /// nothing, everything else drops itself.</summary>
    public static byte DropFor(byte blockId) => blockId switch
    {
        Stone => Cobblestone,
        Branch => Log,
        Limb => Log,
        Sprig => Log,
        Switch => Log,
        Twig => Log,
        Tnt => Air,
        Leaves => Air,
        _ => blockId,
    };

    public record struct Point(int X, int Y);
}
