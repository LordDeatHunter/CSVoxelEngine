using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Shapes;

namespace VoxelEngine.Interaction;

/// <summary>
/// GL-free rules of the double door: two side-hinged blocks (<see cref="BlockType.SideHinged"/>, door halves) side by
/// side with the same facing and opposite hinges, each one's free edge (its latch side) meeting the other's. Nothing
/// is stored: the pair is read off the two cells' state bytes (<see cref="DoorState"/>), so breaking one door leaves
/// the other a plain single door. Shared by the placement (which mirrors the second door of a pair), the toggle
/// (which swings both) and the tests.
/// </summary>
public static class DoubleDoor
{
    /// <summary>The horizontal step from a side-hinged cell to the cell beside its free edge: to the door's right
    /// for a left-hand hinge (east when it faces north), to its left for a right-hand one.</summary>
    public static Vector3 LatchSide(byte state)
    {
        var right = HorizontalFacing.RotateDirection(Vector3.UnitX, DoorState.Facing(state));
        return DoorState.HingeRight(state) ? -right : right;
    }

    /// <summary>
    /// The other door of the double door the side-hinged cell <paramref name="cell"/> (holding
    /// <paramref name="id"/> in <paramref name="state"/>) belongs to: the cell on its latch side, when that holds
    /// the same kind of half with the same facing and the opposite hinge. False for a single door.
    /// </summary>
    public static bool TryGetPartner(ChunkManager chunks, Vector3 cell, ushort id, byte state, out Vector3 partnerCell)
    {
        partnerCell = cell + LatchSide(state);
        if (BlockType.ById[id] is not { SideHinged: true } type) return false;
        if (!chunks.TryGetCell((int)partnerCell.X, (int)partnerCell.Y, (int)partnerCell.Z, out var otherId, out var otherState)
            || BlockType.ById[otherId] is not { SideHinged: true } other)
            return false;
        return (other.UpperHalfId != 0) == (type.UpperHalfId != 0)
               && DoorState.Facing(otherState) == DoorState.Facing(state)
               && DoorState.HingeRight(otherState) != DoorState.HingeRight(state);
    }

    /// <summary>
    /// The state a door placed into <paramref name="cell"/> facing
    /// <paramref name="facing"/> starts with. Beside a single door of the same facing whose latch side is this cell
    /// it takes the opposite hinge and that door's open bit, completing a double door; anywhere else (beside a
    /// door's hinge, a door facing another way or one that already has a partner) it is a plain closed door hinged
    /// on the left.
    /// </summary>
    public static byte PlacedState(ChunkManager chunks, Vector3 cell, int facing)
    {
        foreach (var hingeRight in (ReadOnlySpan<bool>)[true, false])
        {
            var neighbor = cell + LatchSide(DoorState.Encode(facing, false, hingeRight));
            if (!chunks.TryGetCell((int)neighbor.X, (int)neighbor.Y, (int)neighbor.Z, out var otherId, out var otherState)
                || BlockType.ById[otherId] is not { SideHinged: true, UpperHalfId: not 0 }
                || DoorState.Facing(otherState) != facing
                || DoorState.HingeRight(otherState) == hingeRight
                || TryGetPartner(chunks, neighbor, otherId, otherState, out _))
                continue;
            return DoorState.Encode(facing, DoorState.IsOpen(otherState), hingeRight);
        }

        return DoorState.Encode(facing, false);
    }
}
