using System;
using System.Linq;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// Dense chunk cells for the <see cref="ChunkBlocks"/> codec tests (save and network): a seeded
/// cell set with an exact number of distinct ids, and the <see cref="ChunkBlocks.FromDense"/> mode
/// rule to check a decoded storage against.
/// </summary>
internal static class StorageCells
{
    /// <summary>The FromDense rule: one distinct id is Uniform, up to the palette's capacity is
    /// Indexed, more is Direct.</summary>
    internal static ChunkBlocks.StorageMode SmallestModeFor(ushort[] cells)
    {
        var distinct = cells.Distinct().Count();
        if (distinct == 1) return ChunkBlocks.StorageMode.Uniform;
        return distinct <= ChunkBlocks.PaletteCapacity ? ChunkBlocks.StorageMode.Indexed : ChunkBlocks.StorageMode.Direct;
    }

    /// <summary>Dense cells holding <paramref name="distinct"/> ids in a seeded shuffle: id 0 (air)
    /// plus distinct - 1 ids from <paramref name="firstId"/> up, every id used at least once (one
    /// distinct id fills every cell with <paramref name="firstId"/>).</summary>
    internal static ushort[] WithDistinctIds(int distinct, ushort firstId, int seed)
    {
        var cells = new ushort[Chunk.BlockCount];
        if (distinct == 1)
        {
            Array.Fill(cells, firstId);
            return cells;
        }

        for (var i = 0; i < cells.Length; i++)
        {
            var k = i % distinct;
            cells[i] = k == 0 ? BlockType.Air : (ushort)(firstId + k - 1);
        }

        new Random(seed).Shuffle(cells);
        return cells;
    }
}
