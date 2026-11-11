using System.Collections.Generic;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.WorldGen.Hell;

/// <summary>
/// The space a hell tree grows in (<see cref="HellTrees"/>): a cell is free when it lies in the tree's
/// own square of columns and is hell's cavern air (<see cref="HellGenerator.IsCavernAir"/>), the
/// pocket before any flora. A pure function of the cell, so the tree is too. One owner, not
/// thread-safe: the cavern samples are memoized for the one tree it grows.
/// </summary>
internal sealed class HellTreeSpace(int x0, int z0, int size, Dictionary<long, float> samples) : ITreeSpace
{
    /// <inheritdoc />
    public bool IsFree(int x, int y, int z) =>
        x >= x0 && x < x0 + size && z >= z0 && z < z0 + size && HellGenerator.IsCavernAir(x, y, z, samples);
}
