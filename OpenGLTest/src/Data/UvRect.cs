using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// An axis-aligned region of one slice of an array texture, in 0..1 UV space. <see cref="Min"/>
/// is the top-left corner as the image is authored (V grows downwards), matching the chunk
/// mesher. <see cref="Layer"/> is 0 for single-slice textures and the tile for the block atlas.
/// </summary>
public readonly record struct UvRect(Vector2 Min, Vector2 Max, ushort Layer = 0)
{
    /// <summary>The whole texture — what a standalone (non-atlas) image wants.</summary>
    public static readonly UvRect Full = new(Vector2.Zero, Vector2.One);

    /// <summary>One whole tile of the block atlas.</summary>
    public static UvRect Tile(ushort tile) => new(Vector2.Zero, Vector2.One, tile);

    /// <summary>
    /// The four corners in the winding the primitive builders and chunk mesher share:
    /// bottom-left, top-left, top-right, bottom-right.
    /// </summary>
    public void CornersTo(Span<Vector2> corners)
    {
        corners[0] = new Vector2(Min.X, Max.Y);
        corners[1] = Min;
        corners[2] = new Vector2(Max.X, Min.Y);
        corners[3] = new Vector2(Max.X, Max.Y);
    }
}
