using System;

namespace silkgltriangle;

/// <summary>
/// Per-thread scratch buffers for the binary greedy pass of <c>Chunk.GenerateMeshData</c>
/// (~20 KB), so a mesh build allocates nothing beyond its output lists. One instance per
/// thread: the build never awaits, so the pool threads, an immediate remesh on the main thread
/// and a toppling tree's scratch chunk each own theirs and never see another's writes.
/// </summary>
internal sealed class MeshScratch
{
    [ThreadStatic] private static MeshScratch? _current;

    internal static MeshScratch Current => _current ??= new MeshScratch();

    /// <summary>Bit columns per axis (X, Y, Z) over the padded snapshot: column
    /// <c>(row + 1) * 18 + (col + 1)</c> holds one bit per padded coordinate along the axis
    /// (0..17; chunk cells are bits 1..16), set where the cell is a greedy block.</summary>
    internal readonly uint[][] GreedyColumns =
        [new uint[Chunk.PaddedSq], new uint[Chunk.PaddedSq], new uint[Chunk.PaddedSq]];

    /// <summary>Same layout, set where the cell occludes (<see cref="BlockType.Occludes"/>).</summary>
    internal readonly uint[][] OccluderColumns =
        [new uint[Chunk.PaddedSq], new uint[Chunk.PaddedSq], new uint[Chunk.PaddedSq]];

    /// <summary>Face mask of the current cube face per (row, col) column: one bit per depth.</summary>
    internal readonly uint[] Visible = new uint[Chunk.Size * Chunk.Size];

    /// <summary>Open-addressed key table for one depth slice: slot holds plane index + 1, 0 is
    /// empty. A slice has at most 16 x 16 faces, so at most 256 distinct keys.</summary>
    internal const int PlaneTableSize = 512;
    internal readonly int[] PlaneTable = new int[PlaneTableSize];

    /// <summary>Key of each open plane in the current slice.</summary>
    internal readonly int[] PlaneKeys = new int[Chunk.Size * Chunk.Size];

    /// <summary>Bit planes of the current slice, 16 rows each: <c>PlaneRows[plane * 16 + row]</c>
    /// bit <c>col</c>.</summary>
    internal readonly ushort[] PlaneRows = new ushort[Chunk.Size * Chunk.Size * Chunk.Size];
}
