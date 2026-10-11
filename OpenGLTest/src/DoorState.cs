namespace silkgltriangle;

/// <summary>
/// The state byte of an openable block (a trapdoor, a door half; <see cref="BlockType.Openable"/>):
/// bits 0-1 the horizontal facing like every facing block, bit 2 <see cref="OpenBit"/>, the rest
/// zero. The open bit picks which half of the block's eight pre-turned models and box sets is
/// drawn and hit (<see cref="ShapeIndex"/>: the four closed facings, then the four open ones).
/// Which meaning a state byte carries is decided by the block id that owns the cell, so this
/// layout belongs to the openable blocks alone. GL-free and pure.
/// </summary>
public static class DoorState
{
    /// <summary>The bit that marks an openable block open.</summary>
    public const byte OpenBit = 1 << 2;

    /// <summary>How many shapes (models, box sets) an openable block has: four facings, closed
    /// and open.</summary>
    public const int ShapeCount = 8;

    /// <summary>The horizontal facing in the low two bits.</summary>
    public static int Facing(byte state) => state & 3;

    /// <summary>Whether the block is open.</summary>
    public static bool IsOpen(byte state) => (state & OpenBit) != 0;

    /// <summary>The state with the open bit flipped and the facing kept.</summary>
    public static byte Toggle(byte state) => (byte)(state ^ OpenBit);

    /// <summary>The state of a block facing <paramref name="facing"/>, open or closed.</summary>
    public static byte Encode(int facing, bool open) => (byte)((facing & 3) | (open ? OpenBit : 0));

    /// <summary>Index into an openable block's eight shapes: the facing, plus four when open.</summary>
    public static int ShapeIndex(byte state) => state & (ShapeCount - 1);
}
