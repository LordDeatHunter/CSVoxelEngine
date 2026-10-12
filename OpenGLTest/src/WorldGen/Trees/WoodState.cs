using System;
using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen.Trees;

/// <summary>
/// Encoding of the per-block state byte (see <c>Chunk._state</c>) for wood cells of the
/// reworked trees. Three independent fields share the byte:
/// <list type="bullet">
/// <item>bits 0-3: the thickness code <c>n</c>, a trunk diameter of <c>n + 1</c> pixels, so
/// <see cref="FullCube"/> (15) is a whole 16 px cell;</item>
/// <item>bits 4-6: the parent direction, 0..5 in <c>Chunk.CubeFaces</c> order (0 -Z, 1 +Z,
/// 2 -X, 3 +X, 4 +Y, 5 -Y), <see cref="ParentRoot"/> (6) for the tree's root cell and
/// <see cref="ParentDetached"/> (7) for wood that belongs to no tree;</item>
/// <item>bit 7: the shell flag.</item>
/// </list>
/// A zero byte decodes as a 1 px stub pointing at -Z; untouched worldgen state is never read
/// through this codec until the tree rework writes it.
/// </summary>
public static class WoodState
{
    /// <summary>Bits holding the thickness code.</summary>
    public const byte ThicknessMask = 0x0F;

    /// <summary>Shift of the parent direction field.</summary>
    public const int ParentShift = 4;

    /// <summary>Mask of the parent direction field, after shifting.</summary>
    public const byte ParentMask = 0x07;

    /// <summary>Bit marking a shell cell.</summary>
    public const byte ShellBit = 0x80;

    /// <summary>Thickness code of a cell filled edge to edge (16 px).</summary>
    public const int FullCube = 15;

    /// <summary>Largest thickness code the field can hold.</summary>
    public const int MaxThickness = 15;

    /// <summary>Parent code of a tree's root cell: it has no parent cell.</summary>
    public const int ParentRoot = 6;

    /// <summary>Parent code of wood attached to no tree (player-placed).</summary>
    public const int ParentDetached = 7;

    /// <summary>Texture pixels across one block, the unit of <see cref="DiameterPx"/>.</summary>
    public const int PixelsPerBlock = 16;

    /// <summary>State of player-placed wood: a full cube with no tree behind it.</summary>
    public static readonly byte Detached = Encode(FullCube, ParentDetached, false);

    /// <summary>Packs the three fields; throws when a field is out of its range.</summary>
    public static byte Encode(int thickness, int parent, bool shell)
    {
        CheckThickness(thickness);
        CheckParent(parent);
        return (byte)(thickness | parent << ParentShift | (shell ? ShellBit : 0));
    }

    /// <summary>Thickness code, 0..15.</summary>
    public static int Thickness(byte state) => state & ThicknessMask;

    /// <summary>Trunk diameter in pixels, 1..16.</summary>
    public static int DiameterPx(byte state) => Thickness(state) + 1;

    /// <summary>Parent direction code, 0..7.</summary>
    public static int Parent(byte state) => (state >> ParentShift) & ParentMask;

    /// <summary>Whether the cell is its tree's root.</summary>
    public static bool IsRoot(byte state) => Parent(state) == ParentRoot;

    /// <summary>Whether the cell belongs to no tree.</summary>
    public static bool IsDetached(byte state) => Parent(state) == ParentDetached;

    /// <summary>Whether the parent field names a neighboring cell (direction 0..5).</summary>
    public static bool HasParentCell(byte state) => Parent(state) < ParentRoot;

    /// <summary>Whether the shell flag is set.</summary>
    public static bool IsShell(byte state) => (state & ShellBit) != 0;

    /// <summary>Whether the cell is a whole 16 px cube.</summary>
    public static bool IsFullCube(byte state) => Thickness(state) == FullCube;

    /// <summary>Replaces the thickness code, leaving the other fields untouched.</summary>
    public static byte WithThickness(byte state, int thickness)
    {
        CheckThickness(thickness);
        return (byte)(state & ~ThicknessMask | thickness);
    }

    /// <summary>Replaces the parent direction, leaving the other fields untouched.</summary>
    public static byte WithParent(byte state, int parent)
    {
        CheckParent(parent);
        return (byte)(state & ~(ParentMask << ParentShift) | parent << ParentShift);
    }

    /// <summary>Sets or clears the shell flag, leaving the other fields untouched.</summary>
    public static byte WithShell(byte state, bool shell) =>
        (byte)(shell ? state | ShellBit : state & ~ShellBit);

    /// <summary>
    /// The cell offset toward the parent cell, read from <c>Chunk.CubeFaces</c> so the
    /// direction order is defined once by the face table (0 -Z, 1 +Z, 2 -X, 3 +X, 4 +Y, 5 -Y).
    /// False, with a zero offset, for a root or detached cell.
    /// </summary>
    public static bool TryParentOffset(byte state, out int dx, out int dy, out int dz)
    {
        if (!HasParentCell(state))
        {
            dx = dy = dz = 0;
            return false;
        }

        var normal = Chunk.CubeFaces[Parent(state)].normal;
        dx = (int)MathF.Round(normal.X);
        dy = (int)MathF.Round(normal.Y);
        dz = (int)MathF.Round(normal.Z);
        return true;
    }

    private static void CheckThickness(int thickness)
    {
        if (thickness is < 0 or > MaxThickness)
            throw new ArgumentOutOfRangeException(nameof(thickness), thickness, $"Wood thickness must be 0..{MaxThickness}.");
    }

    private static void CheckParent(int parent)
    {
        if (parent is < 0 or > ParentMask)
            throw new ArgumentOutOfRangeException(nameof(parent), parent, $"Wood parent direction must be 0..{ParentMask}.");
    }
}
