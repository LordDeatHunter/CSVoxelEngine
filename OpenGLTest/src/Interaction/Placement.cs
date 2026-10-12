using System.Numerics;
using VoxelEngine.Content.Shapes;

namespace VoxelEngine.Interaction;

/// <summary>
/// GL-free placement rules shared by the block-editing paths and the tests: which cell a
/// placement aimed at a hit face fills, and what it may write over. Aiming at a stack that opts
/// in (<c>replaceableByPlacement</c>, layered snow) fills that stack's own cell and discards it,
/// so a block placed against a partial drift rests on the ground instead of hovering a layer
/// above it. Only a non-solid cell can be taken over; see
/// <see cref="BlockType.IsReplaceableByPlacement"/>.
/// </summary>
public static class Placement
{
    /// <summary>The cell a placement aimed at <paramref name="hit"/> fills: the neighbor across
    /// the hit face, or the hit cell itself when it holds a stack a placement may take over.
    /// <paramref name="hit"/> is the integral cell the raycast reported.</summary>
    public static Vector3 TargetCell(Vector3 hit, Vector3 normal, byte targetId, byte targetState) =>
        BlockType.IsReplaceableByPlacement(targetId, targetState) ? hit : hit + normal;

    /// <summary>Whether a placement may write into a cell that already holds a block: air, a
    /// fluid it displaces, or a stack it takes over. Anything else occupies the cell and the
    /// caller keeps its item.</summary>
    public static bool CanOccupy(byte existingId, byte existingState) =>
        existingId == 0
        || BlockType.IsFluid(existingId)
        || BlockType.IsReplaceableByPlacement(existingId, existingState);

    /// <summary>
    /// The block and state a placement of <paramref name="blockId"/> against a face with outward
    /// normal <paramref name="normal"/> writes. A block with a wall variant
    /// (<see cref="BlockType.WallBlockId"/>, a torch) stays itself on top of a block, becomes the
    /// wall variant on a side, facing along the normal so it leans away from the wall (the facing
    /// is the normal's face index, which puts the wall behind its front, see
    /// <see cref="BlockSupport"/>), and is refused under a ceiling, where nothing could hold it. A
    /// placement that takes over the hit cell itself (<paramref name="fillsHitCell"/>, a partial
    /// snow drift) stands on the floor whatever face was hit, since that face is no wall. Every
    /// other block places as itself with <paramref name="state"/>. Returns false when refused.
    /// </summary>
    public static bool TryMount(byte blockId, byte state, Vector3 normal, bool fillsHitCell,
        out byte placedId, out byte placedState)
    {
        placedId = blockId;
        placedState = state;
        var wall = BlockType.ById[blockId]?.WallBlockId ?? 0;
        if (wall == 0 || fillsHitCell || normal.Y > 0.5f) return true;
        if (normal.Y < -0.5f) return false;

        var facing = HorizontalFacing.FacingOf(normal);
        if (facing < 0) return false;
        placedId = wall;
        placedState = (byte)facing;
        return true;
    }

    /// <summary>
    /// <see cref="TryMount(byte, byte, Vector3, bool, out byte, out byte)"/> for a placement made
    /// looking along <paramref name="viewDirection"/>: a placed block that takes its facing from the
    /// look (<see cref="BlockType.FacingFromLook"/>, stairs) faces <see cref="LookFacing"/> of it,
    /// whatever face was hit. Everything else mounts exactly as the overload without a look.
    /// </summary>
    public static bool TryMount(byte blockId, byte state, Vector3 normal, bool fillsHitCell, Vector3 viewDirection,
        out byte placedId, out byte placedState)
    {
        if (!TryMount(blockId, state, normal, fillsHitCell, out placedId, out placedState)) return false;
        if (BlockType.ById[placedId] is { FacingFromLook: true })
            placedState = (byte)LookFacing(viewDirection);
        return true;
    }

    /// <summary>
    /// The horizontal facing a look along <paramref name="viewDirection"/> points at: the dominant
    /// horizontal axis of the look with its vertical part ignored, so looking down at the floor
    /// still faces the way the player is turned. A look straight up or down faces north.
    /// </summary>
    public static int LookFacing(Vector3 viewDirection)
    {
        var facing = HorizontalFacing.FacingOf(new Vector3(viewDirection.X, 0f, viewDirection.Z));
        return facing < 0 ? 0 : facing;
    }
}
