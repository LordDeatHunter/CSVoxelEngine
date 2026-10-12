using System;
using VoxelEngine.Content.Blocks;

namespace VoxelEngine.Content.Loot;

/// <summary>
/// One thickness band of a <see cref="BlockRenderer.TreeWood"/> block's loot: wood whose
/// thickness code is at least <paramref name="MinThickness"/> (and below the next band's)
/// drops the block itself when <paramref name="Self"/> is set, else rolls
/// <paramref name="Table"/> (<see cref="LootTable.None"/> yields nothing). A block's bands are
/// validated ascending with the first at 0, so every thickness lands in exactly one.
/// </summary>
public readonly record struct WoodLootBand(int MinThickness, LootTable? Table, bool Self)
{
    /// <summary>
    /// The band with the greatest <see cref="MinThickness"/> not above
    /// <paramref name="thickness"/>; the first band when none qualifies (which validated bands,
    /// starting at 0, never leave).
    /// </summary>
    public static WoodLootBand Select(ReadOnlySpan<WoodLootBand> bands, int thickness)
    {
        var chosen = bands[0];
        for (var i = 1; i < bands.Length; i++)
        {
            if (bands[i].MinThickness > thickness) break;
            chosen = bands[i];
        }

        return chosen;
    }
}
