using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// A tree space with no block storage behind it: the terrain is a height delegate (every cell at
/// or below the column's top solid block is ground), optionally pierced by a carved-cell delegate
/// (the cave air under a base that the roots feature may grow into), plus a set of cells already
/// occupied by other trees. <see cref="FromTerrain"/> reads the real, pure worldgen column heights;
/// tests pass a lambda so they need no worldgen. One owner, not thread-safe.
/// </summary>
public sealed class VirtualTreeSpace : ITreeSpace
{
    private readonly Func<int, int, int> _heightAt;
    private readonly Func<int, int, int, bool>? _carvedAt;

    // Membership only, never iterated, so no order can leak into growth output.
    private readonly HashSet<long> _occupied = new();

    /// <param name="heightAt">(x, z) -> the column's top solid block; cells at or below it are terrain.</param>
    /// <param name="carvedAt">(x, y, z) -> whether a terrain cell was emptied by caves; null means none.</param>
    public VirtualTreeSpace(Func<int, int, int> heightAt, Func<int, int, int, bool>? carvedAt = null)
    {
        _heightAt = heightAt ?? throw new ArgumentNullException(nameof(heightAt));
        _carvedAt = carvedAt;
    }

    /// <summary>A space over the generated terrain heights (ravines included), no carved cells.</summary>
    public static VirtualTreeSpace FromTerrain() => new(TerrainHeight);

    // The same expression as TreeGenerator.HeightAt: a static method group, so no closure.
    private static int TerrainHeight(int x, int z) =>
        Chunk.GetColumnData(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            .Heights[((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];

    /// <summary>The column's top solid block.</summary>
    public int GroundHeight(int x, int z) => _heightAt(x, z);

    /// <inheritdoc />
    public bool IsFree(int x, int y, int z)
    {
        if (_occupied.Contains(TreeCellKey.Pack(x, y, z))) return false;
        return y > _heightAt(x, z) || (_carvedAt != null && _carvedAt(x, y, z));
    }

    /// <summary>Marks the cell as taken (by another tree), so it is no longer free.</summary>
    public void Occupy(int x, int y, int z) => _occupied.Add(TreeCellKey.Pack(x, y, z));

    /// <summary>
    /// Marks every cell as taken: a finished tree's cells become obstacles for the next tree of a
    /// batch. The occupied cells are a set, so order and duplicates do not matter.
    /// </summary>
    public void OccupyAll(ReadOnlySpan<TreeCell> cells)
    {
        foreach (var c in cells)
            Occupy(c.X, c.Y, c.Z);
    }

    /// <summary>Whether <see cref="Occupy"/> took the cell.</summary>
    public bool IsOccupied(int x, int y, int z) => _occupied.Contains(TreeCellKey.Pack(x, y, z));

    /// <summary>How many distinct cells are occupied.</summary>
    public int OccupiedCount => _occupied.Count;
}
