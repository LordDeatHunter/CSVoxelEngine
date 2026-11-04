using System.Collections.Generic;
using VoxelEngine.Chunks;
using Xunit;

namespace VoxelEngine.Tests.Chunks;

public sealed class ChunkCellCacheTests
{
    private static readonly int[] Locals = [0, 1, 8, Chunk.SizeMask - 1, Chunk.SizeMask];

    private static long KeyOf(int x, int y, int z) =>
        ChunkKey.Pack(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);

    /// <summary>
    /// Every cell a mesh of a neighbor chunk can read the edited cell through: the face neighbors and,
    /// for a beveled tree-wood shell's cap, the vertical neighbor's horizontal neighbors.
    /// </summary>
    private static IEnumerable<(int X, int Y, int Z)> Readers(int x, int y, int z)
    {
        yield return (x - 1, y, z);
        yield return (x + 1, y, z);
        yield return (x, y, z - 1);
        yield return (x, y, z + 1);
        foreach (var dy in new[] { -1, 1 })
        {
            yield return (x, y + dy, z);
            yield return (x - 1, y + dy, z);
            yield return (x + 1, y + dy, z);
            yield return (x, y + dy, z - 1);
            yield return (x, y + dy, z + 1);
        }
    }

    [Fact]
    public void EveryChunkThatReadsAnEditedCellIsMarked()
    {
        const int Base = 3 * Chunk.Size;
        foreach (var lx in Locals)
        foreach (var ly in Locals)
        foreach (var lz in Locals)
        {
            int x = Base + lx, y = Base + ly, z = Base + lz;
            var touched = new HashSet<long>();
            ChunkCellCache.AddTouchedChunks(x, y, z, touched);

            Assert.Contains(KeyOf(x, y, z), touched);
            foreach (var (rx, ry, rz) in Readers(x, y, z))
                Assert.Contains(KeyOf(rx, ry, rz), touched);
        }
    }

    [Fact]
    public void AnInteriorCellTouchesOnlyItsOwnChunk()
    {
        var touched = new HashSet<long>();
        ChunkCellCache.AddTouchedChunks(3 * Chunk.Size + 8, 3 * Chunk.Size + 8, 3 * Chunk.Size + 8, touched);
        Assert.Single(touched);
    }
}
