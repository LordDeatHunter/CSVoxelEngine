using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Interaction;

/// <summary>
/// GL-free: which object a world cell belongs to. A block that fills several cells (a multiblock's anchor and
/// parts, the two halves of a door or a waystone) is one object to the player, so everything that names or draws
/// "the block under the crosshair" asks here instead of reading the cell's own id, and a new kind of multi-cell
/// block is taught to this one place.
/// </summary>
public static class WholeBlock
{
    /// <summary>
    /// The id of the object the cell (x, y, z) belongs to: a multiblock's anchor for any of its cells (with
    /// <paramref name="anchor"/> its anchor cell, which is only set for a multiblock), the lower half for either
    /// half of a two-cell block, and the cell's own block for anything else.
    /// </summary>
    public static ushort IdAt(ChunkManager chunks, int x, int y, int z, out Vector3 anchor) =>
        Multiblock.TryResolveAnchor(chunks, x, y, z, out anchor, out _, out var anchorType)
            ? anchorType!.Id
            : TallBlock.LowerOf(chunks.GetBlockGlobal(x, y, z));
}
