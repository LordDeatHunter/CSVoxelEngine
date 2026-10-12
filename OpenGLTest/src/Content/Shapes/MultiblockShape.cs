using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Models;
using VoxelEngine.Interaction;

namespace VoxelEngine.Content.Shapes;

/// <summary>
/// The geometry of a multiblock's 2x2x2 volume: how a point, a direction or a whole baked model
/// turns from the authored orientation (the structure's front on north, -Z) to any of the four
/// horizontal facings.
///
/// The rotation is a quarter turn about the vertical axis through the center of the volume's
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
    public static Vector3 RotatePoint(Vector3 local, int facing) =>
        HorizontalFacing.RotatePoint(local, facing, Size);

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
    /// Pre-rotates a whole baked model to <paramref name="facing"/>, so the mesher only indexes an array
    /// instead of transforming quads per cell (see <see cref="HorizontalFacing.RotateModel"/>).
    ///
    /// Every output quad has <c>IsFullFace</c> cleared: the per-corner AO path indexes its
    /// neighbor deltas by <see cref="Chunk.CubeFaces"/> corner order, which a rotated quad's
    /// corners no longer follow, so a multiblock quad must always shade flat (the bake
    /// additionally rejects a full-face quad on a multiblock model, so facing 0 shades the same).
    /// A multiblock carries no cullface, so the one the shared rotation turns never applies.
    /// </summary>
    public static BakedModel Rotate(BakedModel model, int facing) =>
        HorizontalFacing.RotateModel(model, facing, Size);
}
