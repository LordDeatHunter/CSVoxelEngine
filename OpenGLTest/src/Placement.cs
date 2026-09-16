using System.Numerics;

namespace silkgltriangle;

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
    /// <summary>The cell a placement aimed at <paramref name="hit"/> fills: the neighbour across
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
}
