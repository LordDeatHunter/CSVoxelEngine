using System;
using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen.Trees;

/// <summary>
/// The six face directions a tree grows along, in <c>Chunk.CubeFaces</c> order (0 -Z, 1 +Z,
/// 2 -X, 3 +X, 4 +Y, 5 -Y). The offsets are read from the face table once, exactly the way
/// <see cref="WoodState.TryParentOffset"/> reads them, so the direction order is defined in one
/// place and a wood cell's stored parent code and the growth engine always agree.
/// </summary>
public static class TreeDirections
{
    /// <summary>The +Y direction.</summary>
    public const int Up = 4;

    /// <summary>The -Y direction.</summary>
    public const int Down = 5;

    /// <summary>Number of face directions.</summary>
    public const int Count = 6;

    /// <summary>X step of each direction.</summary>
    public static readonly int[] Dx = new int[Count];

    /// <summary>Y step of each direction.</summary>
    public static readonly int[] Dy = new int[Count];

    /// <summary>Z step of each direction.</summary>
    public static readonly int[] Dz = new int[Count];

    static TreeDirections()
    {
        for (var i = 0; i < Count; i++)
        {
            var normal = Chunk.CubeFaces[i].normal;
            Dx[i] = (int)MathF.Round(normal.X);
            Dy[i] = (int)MathF.Round(normal.Y);
            Dz[i] = (int)MathF.Round(normal.Z);
        }

        // Up/Down and the dir ^ 1 pairing below assume this face order.
        if (Dy[Up] != 1 || Dy[Down] != -1)
            throw new InvalidOperationException("Chunk.CubeFaces no longer lists +Y at index 4 and -Y at index 5.");
    }

    /// <summary>The opposite direction (the face table pairs 0/1, 2/3 and 4/5).</summary>
    public static int Opposite(int dir) => dir ^ 1;

    /// <summary>Whether the direction is +Y or -Y.</summary>
    public static bool IsVertical(int dir) => dir >= Up;

    /// <summary>Whether the direction is one of the four horizontal ones.</summary>
    public static bool IsSide(int dir) => dir < Up;
}
