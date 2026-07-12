using System.Collections.Generic;

namespace silkgltriangle;

public class BlockType
{
    public static readonly Dictionary<byte, BlockType> Types = new()
    {
        { 1, new BlockType(new Point(0, 0), new Point(2, 0), new Point(1, 0)) }, // grass
        { 2, new BlockType(new Point(2, 0), new Point(2, 0), new Point(2, 0)) }, // dirt
        { 3, new BlockType(new Point(3, 0), new Point(3, 0), new Point(3, 0)) }, // stone
    };

    // Array-indexed view of Types for the meshing hot path (no dictionary hashing per block)
    public static readonly BlockType?[] ById = BuildById();

    public Point Top, Bottom, Side;

    // Tile per face, indexed in Chunk.CubeFaces order: -Z, +Z, -X, +X, +Y, -Y
    public readonly Point[] FaceTiles;

    public BlockType(Point top, Point bottom, Point side)
    {
        Top = top;
        Bottom = bottom;
        Side = side;
        FaceTiles = [side, side, side, side, top, bottom];
    }

    private static BlockType?[] BuildById()
    {
        var byId = new BlockType?[256];
        foreach (var (id, type) in Types)
            byId[id] = type;
        return byId;
    }

    public record struct Point(int X, int Y);
}
