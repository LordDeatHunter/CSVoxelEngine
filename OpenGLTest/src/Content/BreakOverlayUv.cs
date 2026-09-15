using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Cell-space crop of the block-breaking crack sprite for one hitbox face. The overlay draws
/// each hitbox as a scaled unit cube whose face quads carry 0..1 UVs, which stretches the whole
/// sprite onto thin boxes (snow slabs, tapered arms). Cropping instead samples the sub-rectangle
/// the face actually covers in cell space, so texel density matches a full cube and faces line
/// up in world space. Face order is <c>Chunk.CubeFaces</c> (-Z, +Z, -X, +X, +Y, -Y), matching the
/// shared face VAO the overlay draws.
/// </summary>
public static class BreakOverlayUv
{
    /// <summary>UV sub-rectangle for <paramref name="face"/> of <paramref name="box"/>:
    /// sampled UV = <c>Offset + TexCoord * Scale</c>.</summary>
    public static (Vector2 Offset, Vector2 Scale) ForFace(int face, BlockHitbox box)
    {
        var min = box.Min;
        var size = box.Size;
        return face switch
        {
            0 or 1 => (new Vector2(min.X, min.Y), new Vector2(size.X, size.Y)),
            2 or 3 => (new Vector2(min.Z, min.Y), new Vector2(size.Z, size.Y)),
            _ => (new Vector2(min.X, min.Z), new Vector2(size.X, size.Z)),
        };
    }
}
