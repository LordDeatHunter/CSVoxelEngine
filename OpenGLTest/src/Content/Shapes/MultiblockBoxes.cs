using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Interaction;

namespace VoxelEngine.Content.Shapes;

/// <summary>
/// The shape of a multiblock whose silhouette is not its cubic volume: the anchor's
/// <c>multiblock.boxes</c> (axis-aligned boxes in volume cell space, 0..size, authored with the
/// front on north like the model) turned to each facing and cut up per cell, so every cell of the
/// structure answers the raycast, the outline, the break overlay and body collision with the piece
/// of the shape that lies inside it. Built once at load, so <see cref="At"/> never allocates.
/// GL-free and pure.
/// </summary>
public static class MultiblockBoxes
{
    // A clipped piece thinner than this is a box that only touches the cell's face
    private const float MinExtent = 1e-4f;

    /// <summary>Index of the cell at offset (<paramref name="dx"/>, <paramref name="dy"/>,
    /// <paramref name="dz"/>) from the anchor in a table <see cref="Build"/> made, the order of
    /// <see cref="Multiblock.Offsets"/>.</summary>
    public static int CellIndex(int dx, int dy, int dz, int size) => dx + size * (dy + size * dz);

    /// <summary>
    /// The per-cell boxes of a <paramref name="size"/>-cell volume shaped by
    /// <paramref name="boxes"/>, indexed by facing and then by <see cref="CellIndex"/>, each box in
    /// its own cell's 0..1 space. Throws an <see cref="ArgumentException"/> when a box leaves the
    /// volume or a cell ends up with no box at all: such a cell could be neither aimed at nor mined.
    /// </summary>
    public static BlockHitbox[][][] Build(BlockHitbox[] boxes, int size)
    {
        foreach (var box in boxes)
            if (box.Min.X < 0f || box.Min.Y < 0f || box.Min.Z < 0f
                || box.Max.X > size || box.Max.Y > size || box.Max.Z > size)
                throw new ArgumentException(
                    $"a box from {box.Min} to {box.Max}, outside the {size}-cell volume.");

        var table = new BlockHitbox[4][][];
        for (var facing = 0; facing < 4; facing++)
        {
            var turned = new BlockHitbox[boxes.Length];
            for (var i = 0; i < boxes.Length; i++)
            {
                var a = MultiblockShape.RotatePoint(boxes[i].Min, facing, size);
                var b = MultiblockShape.RotatePoint(boxes[i].Max, facing, size);
                turned[i] = new BlockHitbox(Vector3.Min(a, b), Vector3.Max(a, b));
            }

            var cells = new BlockHitbox[size * size * size][];
            foreach (var (dx, dy, dz) in Multiblock.Offsets(size))
            {
                var clipped = ClipToCell(turned, new Vector3(dx, dy, dz));
                if (clipped.Length == 0)
                    throw new ArgumentException(
                        $"no box in the cell at offset [{dx}, {dy}, {dz}] (facing {facing}); every cell of the volume needs a piece of the shape.");
                cells[CellIndex(dx, dy, dz, size)] = clipped;
            }

            table[facing] = cells;
        }

        return table;
    }

    /// <summary>
    /// The boxes of one cell of a shaped multiblock, from the cell's own block type and state byte
    /// (the facing and the offset from the anchor, <see cref="Multiblock.DecodeState"/>), or null
    /// when the structure declares no shape or the state names a cell outside it, which leaves the
    /// caller its full cube.
    /// </summary>
    public static BlockHitbox[]? At(BlockType type, byte state)
    {
        var anchor = type.Renderer == BlockRenderer.Multiblock ? type : BlockType.ById[type.MultiblockAnchorId];
        if (anchor?.MultiblockCellBoxes is not { } table) return null;

        Multiblock.DecodeState(state, out var facing, out var dx, out var dy, out var dz);
        var size = anchor.MultiblockSize;
        if (dx >= size || dy >= size || dz >= size) return null;
        return table[facing][CellIndex(dx, dy, dz, size)];
    }

    private static BlockHitbox[] ClipToCell(BlockHitbox[] boxes, Vector3 cell)
    {
        var pieces = new List<BlockHitbox>();
        foreach (var box in boxes)
        {
            var min = Vector3.Max(box.Min - cell, Vector3.Zero);
            var max = Vector3.Min(box.Max - cell, Vector3.One);
            if (max.X - min.X < MinExtent || max.Y - min.Y < MinExtent || max.Z - min.Z < MinExtent) continue;
            pieces.Add(new BlockHitbox(min, max));
        }

        return [.. pieces];
    }
}
