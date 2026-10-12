using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// GL-free rules of a block two cells tall (a door, <c>upperHalf</c> in its JSON): a lower half,
/// which is the block's item and what it drops, and an upper half in the cell right above it
/// (<see cref="BlockType.UpperHalfId"/> / <see cref="BlockType.LowerHalfId"/>). Placement writes
/// both with one state byte, breaking either removes the other and drops the lower half once, and
/// an openable pair (<see cref="DoorState"/>) opens and shuts together: <c>Multiblock.Revert</c>
/// in miniature. Shared by the placement, break and toggle paths in <see cref="World"/> and the
/// tests.
/// </summary>
public static class TallBlock
{
    /// <summary>
    /// The other half of the two-cell block <paramref name="id"/> at <paramref name="cell"/>: the
    /// cell above a lower half or below an upper one, and the block that belongs there. False for
    /// every block that is not a half.
    /// </summary>
    public static bool TryGetPartner(byte id, Vector3 cell, out Vector3 partnerCell, out byte partnerId)
    {
        switch (BlockType.ById[id])
        {
            case { UpperHalfId: not 0 } lower:
                partnerCell = cell + Vector3.UnitY;
                partnerId = lower.UpperHalfId;
                return true;
            case { LowerHalfId: not 0 } upper:
                partnerCell = cell - Vector3.UnitY;
                partnerId = upper.LowerHalfId;
                return true;
            default:
                partnerCell = default;
                partnerId = 0;
                return false;
        }
    }

    /// <summary>The block a half drops and is picked as: the lower half, for either half.</summary>
    public static byte LowerOf(byte id) =>
        BlockType.ById[id] is { LowerHalfId: not 0 } upper ? upper.LowerHalfId : id;
}
