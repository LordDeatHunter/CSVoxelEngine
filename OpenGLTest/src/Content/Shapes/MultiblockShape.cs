using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Models;
using VoxelEngine.Interaction;

namespace VoxelEngine.Content.Shapes;

/// <summary>
/// The geometry of a multiblock's cubic volume (<c>size</c> cells along every edge, the anchor
/// block's <see cref="BlockType.MultiblockSize"/>): how a point, a direction or a whole baked model
/// turns from the authored orientation (the structure's front on north, -Z) to any of the four
/// horizontal facings.
///
/// The rotation is a quarter turn about the vertical axis through the center of the volume's
/// footprint, <c>(size / 2, y, size / 2)</c>, which maps the volume onto itself — so a rotated
/// anchor model still sits inside the same cells and no cell bookkeeping depends on the facing.
/// GL-free and pure, so the mesher, the content bake and the tests all share it.
/// </summary>
public static class MultiblockShape
{
    /// <summary>Edge length a multiblock JSON gets when it names none: the 2x2x2 kiln.</summary>
    public const int DefaultSize = 2;

    /// <summary>Smallest legal edge length; a one-cell structure is just a block.</summary>
    public const int MinSize = 2;

    /// <summary>Largest legal edge length: each offset from the anchor is stored in two bits of
    /// the state byte (see <see cref="Multiblock.EncodeState"/>), so 0..3.</summary>
    public const int MaxSize = 4;

    /// <summary>Whether <paramref name="size"/> is a legal multiblock edge length.</summary>
    public static bool IsValidSize(int size) => size is >= MinSize and <= MaxSize;

    /// <summary>
    /// Rotates a point in the volume's local space (0..<paramref name="size"/> on every axis, the
    /// anchor cell's origin at 0) from the authored north facing to <paramref name="facing"/>.
    /// </summary>
    public static Vector3 RotatePoint(Vector3 local, int facing, int size) =>
        HorizontalFacing.RotatePoint(local, facing, size);

    /// <summary>
    /// Rotates a direction the same way <see cref="RotatePoint"/> rotates a point: the same
    /// quarter turns without the translation that keeps the volume in place.
    /// </summary>
    public static Vector3 RotateDirection(Vector3 direction, int facing) =>
        HorizontalFacing.RotateDirection(direction, facing);

    /// <summary>Outward normal of the structure's front for a facing, i.e. the engine face
    /// normal of that face index (north -Z = 0, south +Z = 1, west -X = 2, east +X = 3).</summary>
    public static Vector3 FrontNormal(int facing) => Chunk.CubeFaces[facing].normal;

    /// <summary>
    /// Facing whose <see cref="FrontNormal"/> a normal points along, from its dominant
    /// horizontal axis; -1 when the normal is vertical (there is no vertical facing).
    /// </summary>
    public static int FacingOf(Vector3 normal) => HorizontalFacing.FacingOf(normal);

    /// <summary>
    /// Pre-rotates a whole baked model of a <paramref name="size"/>-cell volume to
    /// <paramref name="facing"/>, so the mesher only indexes an array instead of transforming
    /// quads per cell (see <see cref="HorizontalFacing.RotateModel"/>).
    ///
    /// Every output quad has <c>IsFullFace</c> cleared: the per-corner AO path indexes its
    /// neighbor deltas by <see cref="Chunk.CubeFaces"/> corner order, which a rotated quad's
    /// corners no longer follow, so a multiblock quad must always shade flat (the bake
    /// additionally rejects a full-face quad on a multiblock model, so facing 0 shades the same).
    /// A multiblock carries no cullface, so the one the shared rotation turns never applies.
    /// </summary>
    public static BakedModel Rotate(BakedModel model, int facing, int size) =>
        HorizontalFacing.RotateModel(model, facing, size);
}
