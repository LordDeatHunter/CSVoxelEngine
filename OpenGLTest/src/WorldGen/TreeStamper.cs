using System.Collections.Generic;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.WorldGen;

/// <summary>
/// Writes the trees crossing a chunk into its blocks and state bytes during worldgen: the
/// <see cref="TreeBatches"/> shapes of every nearby cell, wood before leaves, plus the roots a
/// cave mouth under a trunk needs. Runs on the generation workers;
/// <see cref="TreeGenerator.Stamp"/> forwards here.
/// </summary>
internal static class TreeStamper
{
    /// <summary>
    /// Stamps every tree whose volume can intersect this chunk into <paramref name="blocks"/>,
    /// writing each cell's state byte into <paramref name="state"/> (allocated on the first
    /// write). Runs on a generation worker thread, after the cave carvers, before the solid
    /// recount. Trees are grown per 64x64 cell (<see cref="TreeBatch"/>): the chunk reads every
    /// cell whose reach-expanded box meets it, visited in cell order (x, then z) and within a
    /// cell in placement-hash order. Every wood cell goes in first (into air or leaves), then the
    /// leaves (into air only), so a crossing canopy never overwrites wood. That fixed order is
    /// what settles the leftover overlap deterministically: a cell's trees and a neighbor cell's
    /// trees (which grew against this cell's SOLO shapes, not its batch) can still meet at the
    /// border, and a tree's unchecked base cell can sit in an earlier tree's leaves -- wood is
    /// first-come, leaves never go over anything.
    /// </summary>
    public static void Stamp(ushort[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        var r = TreeGenerator.ReachHorizontal;
        var x1 = worldX0 + Chunk.Size;
        var y1 = worldY0 + Chunk.Size;
        var z1 = worldZ0 + Chunk.Size;
        var cx0 = TreeGenerator.CellOf(worldX0 - r);
        var cx1 = TreeGenerator.CellOf(x1 - 1 + r);
        var cz0 = TreeGenerator.CellOf(worldZ0 - r);
        var cz1 = TreeGenerator.CellOf(z1 - 1 + r);

        List<TreeBatches.TreeShape>? near = null;
        for (var cx = cx0; cx <= cx1; cx++)
        for (var cz = cz0; cz <= cz1; cz++)
        {
            var batch = TreeBatches.BatchOf(cx, cz);
            if (batch.MaxY < worldY0 || batch.MinY >= y1)
                continue;

            foreach (var shape in batch.Trees)
            {
                if (shape.X + r < worldX0 || shape.X - r >= x1 || shape.Z + r < worldZ0 || shape.Z - r >= z1)
                    continue;
                if (shape.MinY >= y1 || shape.MaxY < worldY0)
                    continue;
                near ??= new List<TreeBatches.TreeShape>(4);
                near.Add(shape);
            }
        }

        if (near == null) return;

        foreach (var shape in near)
        {
            var cells = shape.Cells;
            for (var i = 0; i < shape.WoodCount; i++)
            {
                if (!TryLocal(cells[i], worldX0, worldY0, worldZ0, out var index)) continue;
                var current = blocks[index];
                if (current != BlockType.Air && !TreeGenerator.LeafById[current]) continue;
                blocks[index] = cells[i].Block;
                state ??= new byte[Chunk.BlockCount];
                state[index] = cells[i].State;
            }

            StampRoots(blocks, ref state, worldX0, worldY0, worldZ0, shape);
        }

        foreach (var shape in near)
        {
            var cells = shape.Cells;
            for (var i = shape.WoodCount; i < cells.Length; i++)
            {
                if (!TryLocal(cells[i], worldX0, worldY0, worldZ0, out var index)) continue;
                if (blocks[index] != BlockType.Air) continue;
                blocks[index] = cells[i].Block;
                state ??= new byte[Chunk.BlockCount];
                state[index] = cells[i].State;
            }
        }
    }

    private static bool TryLocal(in TreeCell cell, int worldX0, int worldY0, int worldZ0, out int index)
    {
        var lx = cell.X - worldX0;
        var ly = cell.Y - worldY0;
        var lz = cell.Z - worldZ0;
        if ((uint)lx >= Chunk.Size || (uint)ly >= Chunk.Size || (uint)lz >= Chunk.Size)
        {
            index = 0;
            return false;
        }

        index = Chunk.Idx(lx, ly, lz);
        return true;
    }

    // A cave mouth can open under a trunk after the tree was grown over the heightmap; grow the
    // trunk down through post-carve air so it reaches solid ground. Only the chunk holding the
    // base cell walks, and it stops at its own floor, so roots never cross a chunk floor: a chunk
    // reads only its own blocks, every chunk stays a pure function of position, and no root stub
    // can be left hanging in a cave below.
    private static void StampRoots(ushort[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0,
        TreeBatches.TreeShape shape)
    {
        var lx = shape.X - worldX0;
        var lz = shape.Z - worldZ0;
        var baseLy = shape.BaseY - worldY0;
        if ((uint)lx >= Chunk.Size || (uint)lz >= Chunk.Size || (uint)baseLy >= Chunk.Size)
            return;
        if (shape.WoodCount == 0) return;

        var depth = TreeRasterizer.ReachOf(shape.Species).Down;
        var rootState = WoodState.Encode(WoodState.Thickness(shape.Cells[0].State), TreeDirections.Up, false);
        for (var d = 1; d <= depth; d++)
        {
            var ly = baseLy - d;
            if (ly < 0) break;

            var index = Chunk.Idx(lx, ly, lz);
            if (blocks[index] != BlockType.Air) break;
            blocks[index] = shape.Species.WoodId;
            state ??= new byte[Chunk.BlockCount];
            state[index] = rootState;
        }
    }
}
