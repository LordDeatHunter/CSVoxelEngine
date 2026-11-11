using System.Runtime.CompilerServices;

namespace VoxelEngine.Content.Structures;

/// <summary>
/// The packed cell a structure template, the battle tower's shell and generator and the tower ruin hand around as an
/// <see cref="int"/>: the block id in bits 0-15 and the state byte in bits 16-23, so a packed cell is never negative and
/// bits 24-31 stay clear. <see cref="None"/> (-1) is a cell that writes nothing; 0 is air. A block with state 0 packs to
/// its own id, so the tower shell returns a bare id as a cell. Callers check <c>cell &lt; 0</c> before decoding.
/// </summary>
internal static class StructureCell
{
    /// <summary>A cell that writes nothing (air is 0, a real write).</summary>
    public const int None = -1;

    private const int BlockMask = 0xFFFF;
    private const int StateShift = 16;

    /// <summary>The cell writing <paramref name="block"/> with state byte <paramref name="state"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Pack(ushort block, byte state) => block | state << StateShift;

    /// <summary>The block a written (non-negative) cell holds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort BlockOf(int cell) => (ushort)(cell & BlockMask);

    /// <summary>The state byte a written (non-negative) cell holds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte StateOf(int cell) => (byte)(cell >> StateShift);

    /// <summary>A written cell with its block swapped for <paramref name="block"/>, keeping its state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WithBlock(int cell, ushort block) => (cell & ~BlockMask) | block;
}
