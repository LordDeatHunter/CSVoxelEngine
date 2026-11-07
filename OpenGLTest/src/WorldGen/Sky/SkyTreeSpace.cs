using System.Collections.Generic;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.WorldGen.Sky;

/// <summary>
/// The space a sky tree grows in (<see cref="SkyTrees"/>): a cell is free when it lies in the tree's own
/// square of columns, inside the island band, and outside every island's cells (the sky before any flora).
/// A pure function of the cell, so the tree is too. One owner, not thread-safe: each column's island cells
/// are memoized for the one tree it grows.
/// </summary>
internal sealed class SkyTreeSpace(int x0, int z0, int size) : ITreeSpace
{
    private readonly Dictionary<long, (int Bottom, int Top)> _columns = new();

    /// <inheritdoc />
    public bool IsFree(int x, int y, int z)
    {
        if (x < x0 || x >= x0 + size || z < z0 || z >= z0 + size) return false;
        if (y < WorldGenerationConfig.SkyIslandBottomY || y > WorldGenerationConfig.SkyIslandTopY) return false;

        var key = ((long)x << 32) | (uint)z;
        if (!_columns.TryGetValue(key, out var column))
        {
            // Open sky reads as an empty range (top under bottom)
            column = SkyIslandGenerator.TryGetColumn(x, z, out var bottom, out var top) ? (bottom, top) : (0, -1);
            _columns[key] = column;
        }

        return y < column.Bottom || y > column.Top;
    }
}
