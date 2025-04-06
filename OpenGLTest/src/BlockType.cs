using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

public class BlockType(BlockType.Point top, BlockType.Point bottom, BlockType.Point side)
{
    public static readonly Dictionary<byte, BlockType> Types = new()
    {
        { 1, new BlockType(new Point(0, 0), new Point(2, 0), new Point(1, 0)) }, // grass
        { 2, new BlockType(new Point(2, 0), new Point(2, 0), new Point(2, 0)) }, // dirt
        { 3, new BlockType(new Point(3, 0), new Point(3, 0), new Point(3, 0)) }, // stone
    };

    public Point Top = top, Bottom = bottom, Side = side;

    public Point GetTile(Vector3 normal)
    {
        if (normal == Vector3.UnitY) return Top;
        if (normal == -Vector3.UnitY) return Bottom;
        return Side;
    }

    public record struct Point(int X, int Y);
}