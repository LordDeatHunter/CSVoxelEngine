using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Models;

namespace VoxelEngine.Content.Shapes;

/// <summary>
/// Quarter turns about the vertical axis through the middle of a <c>size</c>-wide footprint,
/// <c>(size / 2, y, size / 2)</c>, which map the footprint onto itself: the one definition of how
/// anything authored facing north (its front on -Z) turns to another horizontal facing. A facing
/// is the engine face index of the front's outward normal (north -Z = 0, south +Z = 1,
/// west -X = 2, east +X = 3). A multiblock turns its 2x2x2 volume with it (size 2, see
/// <see cref="MultiblockShape"/>) and a single-cell facing block (a wall torch, size 1) its cell.
/// GL-free and pure, so the content bake, the mesher's pre-rotated models and the tests share it.
/// </summary>
public static class HorizontalFacing
{
    /// <summary>
    /// Rotates a point in the footprint's local space (0..<paramref name="size"/> on X and Z)
    /// from the authored north facing to <paramref name="facing"/>.
    /// </summary>
    public static Vector3 RotatePoint(Vector3 local, int facing, float size) => facing switch
    {
        1 => new Vector3(size - local.X, local.Y, size - local.Z),
        2 => new Vector3(local.Z, local.Y, size - local.X),
        3 => new Vector3(size - local.Z, local.Y, local.X),
        _ => local,
    };

    /// <summary>
    /// Rotates a direction the same way <see cref="RotatePoint"/> rotates a point: the same
    /// quarter turns without the translation that keeps the footprint in place.
    /// </summary>
    public static Vector3 RotateDirection(Vector3 direction, int facing) => facing switch
    {
        1 => new Vector3(-direction.X, direction.Y, -direction.Z),
        2 => new Vector3(direction.Z, direction.Y, -direction.X),
        3 => new Vector3(-direction.Z, direction.Y, direction.X),
        _ => direction,
    };

    /// <summary>Engine face index <paramref name="face"/> turns into: the four sides turn with
    /// the facing, top and bottom stay where they are.</summary>
    public static int RotateFace(int face, int facing) =>
        face >= 4 ? face : ModelBaker.DominantFace(RotateDirection(Chunk.CubeFaces[face].normal, facing));

    /// <summary>
    /// Pre-rotates a whole baked model to <paramref name="facing"/>, so the mesher only indexes an
    /// array instead of transforming quads per cell. Positions and normals turn, the face index is
    /// recomputed from the turned normal and the cullface turns with the sides; UVs, tile and shade
    /// ride along unchanged.
    ///
    /// Every output quad has <c>IsFullFace</c> cleared: the per-corner AO path indexes its
    /// neighbor deltas by <see cref="Chunk.CubeFaces"/> corner order, which a rotated quad's
    /// corners no longer follow, so a turned quad must always shade flat (the bake rejects a
    /// full-face quad on every model it turns, so facing 0 shades the same).
    /// </summary>
    public static BakedModel RotateModel(BakedModel model, int facing, float size)
    {
        var quads = new BakedQuad[model.Quads.Length];
        for (var i = 0; i < quads.Length; i++)
        {
            var quad = model.Quads[i];
            var positions = new Vector3[quad.Positions.Length];
            for (var j = 0; j < positions.Length; j++)
                positions[j] = RotatePoint(quad.Positions[j], facing, size);

            var normal = RotateDirection(quad.Normal, facing);
            quads[i] = quad with
            {
                Positions = positions,
                Normal = normal,
                Face = ModelBaker.DominantFace(normal),
                CullFace = quad.CullFace < 0 ? quad.CullFace : RotateFace(quad.CullFace, facing),
                IsFullFace = false,
            };
        }

        return model with { Quads = quads };
    }

    /// <summary>A cell-local box (size 1) turned to <paramref name="facing"/>: both corners turn,
    /// and the result is re-sorted into a min and a max corner.</summary>
    public static BlockHitbox RotateBox(BlockHitbox box, int facing)
    {
        var a = RotatePoint(box.Min, facing, 1f);
        var b = RotatePoint(box.Max, facing, 1f);
        return new BlockHitbox(Vector3.Min(a, b), Vector3.Max(a, b));
    }

    /// <summary>Every box of <paramref name="boxes"/> turned to each of the four facings, indexed
    /// by facing (<c>[0]</c> is the authored set itself).</summary>
    public static BlockHitbox[][] RotateBoxes(BlockHitbox[] boxes)
    {
        var rotated = new BlockHitbox[4][];
        rotated[0] = boxes;
        for (var facing = 1; facing < 4; facing++)
        {
            rotated[facing] = new BlockHitbox[boxes.Length];
            for (var i = 0; i < boxes.Length; i++)
                rotated[facing][i] = RotateBox(boxes[i], facing);
        }
        return rotated;
    }

    /// <summary>
    /// Facing whose front normal a normal points along, from its dominant horizontal axis; -1
    /// when the normal is vertical (there is no vertical facing).
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
}
