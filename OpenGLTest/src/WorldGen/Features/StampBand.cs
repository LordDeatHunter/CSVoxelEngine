using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// The inclusive world-y range a feature stamp can write, empty when <see cref="Min"/> is above
/// <see cref="Max"/>. Contract: a feature's <c>WriteBand</c> contains every world y its
/// <c>Stamp</c> can change, for any input it is given in production. It is derived from the
/// stamp's own code (where it writes relative to the column heights, water levels and its own
/// reach), never from tuning, so <see cref="ChunkTerrainFill"/> only ever skips a stamp that
/// cannot write a cell in the chunk, and skipping it is bit-identical.
/// </summary>
internal readonly record struct StampBand(int Min, int Max)
{
    /// <summary>The band that reaches every chunk: a stamp with no provable bound.</summary>
    internal static readonly StampBand Everywhere = new(int.MinValue, int.MaxValue);

    /// <summary>Whether world y <paramref name="y"/> lies inside the band.</summary>
    internal bool Contains(int y) => y >= Min && y <= Max;

    /// <summary>Whether the band overlaps the chunk spanning world y
    /// <paramref name="worldY0"/>..<paramref name="worldY0"/> + 15, i.e. whether the stamp may write
    /// any cell of that chunk.</summary>
    internal bool Reaches(int worldY0) => Min <= Max && Max >= worldY0 && Min <= worldY0 + Chunk.Size - 1;

    /// <summary>The cell one above each column's height, over a whole chunk column: the band every
    /// surface plant shares (it writes at <c>height + 1</c> and nowhere else).</summary>
    internal static StampBand AboveSurface(ColumnData column) => new(column.MinHeight + 1, column.MaxHeight + 1);
}
