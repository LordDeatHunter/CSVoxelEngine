using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// An axis-aligned region of a texture in 0..1 UV space. <see cref="Min"/> is the top-left
/// corner as the atlas is authored (V grows downwards), matching the chunk mesher.
/// </summary>
public readonly record struct UvRect(Vector2 Min, Vector2 Max)
{
    /// <summary>The whole texture — what a standalone (non-atlas) image wants.</summary>
    public static readonly UvRect Full = new(Vector2.Zero, Vector2.One);

    /// <summary>One tile of a square atlas, addressed in tile coordinates.</summary>
    public static UvRect Tile(int tileX, int tileY, int tilesPerSide)
    {
        var size = 1f / tilesPerSide;
        var min = new Vector2(tileX * size, tileY * size);
        return new UvRect(min, min + new Vector2(size));
    }

    /// <summary>One tile of the block atlas.</summary>
    public static UvRect Tile(BlockType.Point tile) =>
        Tile(tile.X, tile.Y, Chunk.AtlasTilesPerSide);

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
