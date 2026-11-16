namespace VoxelEngine.Interaction;

/// <summary>
/// The state byte of an openable block (a trapdoor, a door half; <see cref="BlockType.Openable"/>):
/// bits 0-1 the horizontal facing like every facing block, bit 2 <see cref="OpenBit"/>, bit 3
/// <see cref="HingeRightBit"/> on a side-hinged block (a door, <see cref="BlockType.SideHinged"/>), the rest
/// zero. The open bit picks which half of the block's eight pre-turned models and box sets is
/// drawn and hit (<see cref="BlockType.ShapeIndex"/>: the four closed facings, then the four open ones), and
/// the hinge bit picks the mirrored eight that follow them (<see cref="HingedShapeCount"/>).
/// Which meaning a state byte carries is decided by the block id that owns the cell, so this
/// layout belongs to the openable blocks alone. GL-free and pure.
/// </summary>
public static class DoorState
{
    /// <summary>The bit that marks an openable block open.</summary>
    public const byte OpenBit = 1 << 2;

    /// <summary>The bit that hangs a side-hinged block (a door) on its right-hand edge instead of its left: the
    /// mirrored half of a double door (<see cref="DoubleDoor"/>). Always clear on any other openable block.</summary>
    public const byte HingeRightBit = 1 << 3;

    /// <summary>How many shapes (models, box sets) an openable block has: four facings, closed
    /// and open.</summary>
    public const int ShapeCount = 8;

    /// <summary>How many shapes a side-hinged block has: <see cref="ShapeCount"/> hinged left, then the same
    /// mirrored for a right-hand hinge.</summary>
    public const int HingedShapeCount = 2 * ShapeCount;

    /// <summary>The horizontal facing in the low two bits.</summary>
    public static int Facing(byte state) => state & 3;

    /// <summary>Whether the block is open.</summary>
    public static bool IsOpen(byte state) => (state & OpenBit) != 0;

    /// <summary>Whether a side-hinged block hangs on its right-hand edge.</summary>
    public static bool HingeRight(byte state) => (state & HingeRightBit) != 0;

    /// <summary>The state with the open bit set to <paramref name="open"/> and the facing and hinge kept.</summary>
    public static byte WithOpen(byte state, bool open) => (byte)(open ? state | OpenBit : state & ~OpenBit);

    /// <summary>The state of a block facing <paramref name="facing"/>, open or closed, hinged left or right.</summary>
    public static byte Encode(int facing, bool open, bool hingeRight = false) =>
        (byte)((facing & 3) | (open ? OpenBit : 0) | (hingeRight ? HingeRightBit : 0));
}
