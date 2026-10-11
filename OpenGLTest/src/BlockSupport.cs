using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// GL-free support rule of the <see cref="BlockType.NeedsSupport"/> blocks, shared by placement,
/// every path that pops an unsupported block (mining, blasts, felling) and the tests: which
/// neighbour holds a cell up. A floor block (a pebble pile, a flower, a floor torch) stands on the
/// cell below; a <see cref="BlockType.SupportAttached"/> block (a wall torch) hangs off the cell
/// behind its front, opposite the horizontal facing in its state byte's low two bits; a
/// <see cref="BlockType.SupportAbove"/> block (a hanging lantern) hangs from the cell above it,
/// so it is only placed against a block's underside, or wherever a solid block stands over it.
/// </summary>
public static class BlockSupport
{
    /// <summary>
    /// The cell holding up a support-fragile <paramref name="blockId"/> with state byte
    /// <paramref name="state"/> at <paramref name="cell"/>; false for a block that needs no support.
    /// </summary>
    public static bool TryGetSupportCell(byte blockId, byte state, Vector3 cell, out Vector3 support)
    {
        if (BlockType.ById[blockId] is not { NeedsSupport: true } type)
        {
            support = default;
            return false;
        }

        support = cell + SupportOffset(type, state);
        return true;
    }

    /// <summary>Offset from a support-fragile block's cell to the cell holding it up: down for a
    /// floor block, back through the wall for an attached one (the opposite of its front), and up
    /// for a hanging one.</summary>
    public static Vector3 SupportOffset(BlockType type, byte state) =>
        type.SupportAttached ? -Chunk.CubeFaces[state & 3].normal
        : type.SupportAbove ? Vector3.UnitY
        : -Vector3.UnitY;

    /// <summary>Whether the block at <paramref name="cell"/> is held up by the cell
    /// <paramref name="holder"/>, i.e. loses its support when that cell is emptied.</summary>
    public static bool IsHeldUpBy(byte blockId, byte state, Vector3 cell, Vector3 holder) =>
        TryGetSupportCell(blockId, state, cell, out var support) && support == holder;
}
