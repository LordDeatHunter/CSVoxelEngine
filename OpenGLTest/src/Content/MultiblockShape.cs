using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The geometry of a multiblock's 2x2x2 volume: how a point, a direction or a whole baked model
/// turns from the authored orientation (the structure's front on north, -Z) to any of the four
/// horizontal facings.
///
/// The rotation is a quarter turn about the vertical axis through the centre of the volume's
/// footprint, <c>(1, y, 1)</c>, which maps the volume onto itself — so a rotated anchor model
/// still sits inside the same eight cells and no cell bookkeeping depends on the facing.
/// GL-free and pure, so the mesher, the content bake and the tests all share it.
/// </summary>
public static class MultiblockShape
{
    /// <summary>Edge length of a multiblock volume in cells. The only definition of it;
    /// <see cref="Multiblock"/> references this rather than repeating the literal.</summary>
    public const int Size = 2;

    /// <summary>
    /// Rotates a point in the volume's local space (0..<see cref="Size"/> on every axis, the
    /// anchor cell's origin at 0) from the authored north facing to <paramref name="facing"/>.
    /// </summary>
    public static Vector3 RotatePoint(Vector3 local, int facing) => facing switch
    {
        1 => new Vector3(Size - local.X, local.Y, Size - local.Z),
        2 => new Vector3(local.Z, local.Y, Size - local.X),
        3 => new Vector3(Size - local.Z, local.Y, local.X),
        _ => local,
    };

    /// <summary>
    /// Rotates a direction the same way <see cref="RotatePoint"/> rotates a point: the same
    /// quarter turns without the translation that keeps the volume in place.
    /// </summary>
    public static Vector3 RotateDirection(Vector3 direction, int facing) => facing switch
    {
        1 => new Vector3(-direction.X, direction.Y, -direction.Z),
        2 => new Vector3(direction.Z, direction.Y, -direction.X),
        3 => new Vector3(-direction.Z, direction.Y, direction.X),
        _ => direction,
    };

    /// <summary>Outward normal of the structure's front for a facing, i.e. the engine face
    /// normal of that face index (north -Z = 0, south +Z = 1, west -X = 2, east +X = 3).</summary>
    public static Vector3 FrontNormal(int facing) => Chunk.CubeFaces[facing].normal;

    /// <summary>
    /// Facing whose <see cref="FrontNormal"/> a normal points along, from its dominant
    /// horizontal axis; -1 when the normal is vertical (there is no vertical facing).
    /// </summary>
    public static int FacingOf(Vector3 normal)
    {
        var ax = MathF.Abs(normal.X);
        var az = MathF.Abs(normal.Z);
        if (ax <= 0f && az <= 0f) return -1;
        if (MathF.Abs(normal.Y) > ax && MathF.Abs(normal.Y) > az) return -1;
        if (az >= ax) return normal.Z < 0f ? 0 : 1;
        return normal.X < 0f ? 2 : 3;
    }

    /// <summary>
    /// Pre-rotates a whole baked model to <paramref name="facing"/>, so the mesher only indexes an array
    /// instead of transforming quads per cell. Positions and normals turn, the face index is
    /// recomputed from the turned normal, and UVs, tile, shade and cullface ride along unchanged.
    ///
    /// Every output quad has <c>IsFullFace</c> cleared: the per-corner AO path indexes its
    /// neighbour deltas by <see cref="Chunk.CubeFaces"/> corner order, which a rotated quad's
    /// corners no longer follow, so a multiblock quad must always shade flat (the bake
    /// additionally rejects a full-face quad on a multiblock model, so facing 0 shades the same).
    /// </summary>
    public static BakedModel Rotate(BakedModel model, int facing)
    {
        var quads = new BakedQuad[model.Quads.Length];
        for (var i = 0; i < quads.Length; i++)
        {
            var quad = model.Quads[i];
            var positions = new Vector3[quad.Positions.Length];
            for (var j = 0; j < positions.Length; j++)
                positions[j] = RotatePoint(quad.Positions[j], facing);

            var normal = RotateDirection(quad.Normal, facing);
            quads[i] = quad with
            {
                Positions = positions,
                Normal = normal,
                Face = ModelBaker.DominantFace(normal),
                IsFullFace = false,
            };
        }

        return model with { Quads = quads };
    }
}
