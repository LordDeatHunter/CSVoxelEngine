using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Interaction;

namespace VoxelEngine.Content.Shapes;

/// <summary>
/// The one resolver of a cell's boxes from the live world, shared by the raycast, the hover
/// outline, the break overlay, the mining pop and shape collision, so all of them agree with what
/// the mesher draws: tree wood follows its thickness and connections (<see cref="TreeWoodShape"/>),
/// layered snow its stack depth (<see cref="SnowShape"/>), a pane its arms (<see cref="PaneShape"/>),
/// a facing block its facing (<see cref="BlockType.RotatedHitboxes"/>, plus the open bit of an
/// openable block, <see cref="DoorState"/>), and everything else its
/// registered <see cref="BlockType.Hitboxes"/>. Only thin and beveled tree wood allocate.
/// </summary>
public static class BlockShape
{
    private static readonly BlockHitbox[] FullCube = [BlockHitbox.FullCube];

    /// <summary>Cell-local boxes of block <paramref name="id"/> at world cell (x, y, z), reading
    /// whatever state and neighbors its shape depends on through <paramref name="chunks"/>.</summary>
    public static BlockHitbox[] BoxesAt(byte id, ChunkManager chunks, int x, int y, int z)
    {
        var type = BlockType.ById[id];
        if (type == null) return FullCube;
        if (BlockType.IsTreeWood(id)) return TreeWoodShape.GetBoxesAt(id, chunks, x, y, z);
        if (type.Renderer == BlockRenderer.SnowLayers)
            return SnowShape.GetBoxes(x, y, z, chunks.GetStateGlobal(x, y, z), type.StackMax);
        if (type.Renderer == BlockRenderer.Pane) return PaneShape.GetBoxesAt(chunks, x, y, z);
        if (type.RotatedHitboxes is { } rotated)
            return rotated[BlockType.ShapeIndex(chunks.GetStateGlobal(x, y, z), rotated.Length)];
        return type.Hitboxes;
    }
}
