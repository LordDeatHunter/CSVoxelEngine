using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Shapes;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Simulation;

/// <summary>
/// GL-free: the points of each block of a felled tree that touch the ground, so a thin branch rests
/// on its real underside and not on the bottom of the cell it grew in. A leaf (or a full-cube
/// trunk) is the whole cell, so its points are the six face centers; a thinner wood block reaches
/// only half its diameter from the center, except along a direction it connects on, where its tube
/// runs out to the cell face.
/// </summary>
internal static class FellContacts
{
    /// <summary>The world-space contact points of block <paramref name="index"/> of
    /// <paramref name="blocks"/> in their standing pose.</summary>
    public static Vector3[][] Build(IReadOnlyList<TopplingTree.FellBlock> blocks)
    {
        var byCell = new Dictionary<long, int>(blocks.Count);
        for (var i = 0; i < blocks.Count; i++)
            byCell[Key(blocks[i].Cell)] = i;

        var result = new Vector3[blocks.Count][];
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var center = block.Cell + new Vector3(0.5f);
            var half = BlockType.IsTreeWood(block.Id) ? WoodState.DiameterPx(block.State) / (2f * WoodState.PixelsPerBlock) : 0.5f;
            var points = new Vector3[TreeDirections.Count];
            for (var dir = 0; dir < TreeDirections.Count; dir++)
            {
                var step = new Vector3(TreeDirections.Dx[dir], TreeDirections.Dy[dir], TreeDirections.Dz[dir]);
                var reach = half;
                if (half < 0.5f && byCell.TryGetValue(Key(block.Cell + step), out var n)
                    && TreeWoodShape.Connects(block.Id, block.State, blocks[n].Id, blocks[n].State, dir))
                    reach = 0.5f;
                points[dir] = center + step * reach;
            }
            result[i] = points;
        }
        return result;
    }

    private static long Key(Vector3 cell) => CellKey.Pack((int)cell.X, (int)cell.Y, (int)cell.Z);
}
