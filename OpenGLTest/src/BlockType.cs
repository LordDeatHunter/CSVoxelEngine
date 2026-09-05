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

    public static readonly Dictionary<byte, BlockType> Types = new()
    {
        { 1, new BlockType(new Point(0, 0), new Point(2, 0), new Point(1, 0)) }, // grass
        { 2, new BlockType(new Point(2, 0), new Point(2, 0), new Point(2, 0)) }, // dirt
        { 3, new BlockType(new Point(0, 1), new Point(0, 1), new Point(0, 1)) }, // stone
        { 4, new BlockType(new Point(3, 0), new Point(3, 0), new Point(3, 0)) }, // cobblestone
        { 5, new BlockType(new Point(2, 1), new Point(2, 1), new Point(1, 1)) }, // tnt
    };

    // Array-indexed view of Types for the meshing hot path (no dictionary hashing per block)
    public static readonly BlockType?[] ById = BuildById();

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

    /// <summary>Block id dropped when a block is mined; stone drops cobblestone, TNT drops
    /// nothing (mining it primes it instead), everything else drops itself.</summary>
    public static byte DropFor(byte blockId) => blockId switch
    {
        Stone => Cobblestone,
        Tnt => Air,
        _ => blockId,
    };

    public record struct Point(int X, int Y);
}
