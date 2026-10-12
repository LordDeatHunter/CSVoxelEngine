using System;

namespace VoxelEngine.Chunks;

/// <summary>
/// Per-thread scratch buffers for the binary greedy pass of <c>Chunk.GenerateMeshData</c>
/// (~22 KB), so a mesh build allocates nothing beyond its output lists. One instance per
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
    internal const int PlaneTableBits = 9;
    internal const int PlaneTableSize = 1 << PlaneTableBits;
    internal readonly int[] PlaneTable = new int[PlaneTableSize];

    /// <summary>Key of each open plane in the current slice.</summary>
    internal readonly int[] PlaneKeys = new int[Chunk.Size * Chunk.Size];

    /// <summary>Parallel 48-bit light key of each open plane: the four 12-bit smooth corner
    /// light codes (<c>Chunk.SmoothCornerLight</c>), corner j at bit 12j. A plane matches a face
    /// only when both this and <see cref="PlaneKeys"/> are equal.</summary>
    internal readonly ulong[] PlaneLightKeys = new ulong[Chunk.Size * Chunk.Size];

    /// <summary>Color key of each plane, parallel to <see cref="PlaneLightKeys"/>: the four
    /// 10-bit <c>LightTint</c> corner codes, corner j at bit 10j. A plane matches a face only
    /// when this matches too.</summary>
    internal readonly ulong[] PlaneTintKeys = new ulong[Chunk.Size * Chunk.Size];

    /// <summary>Bit planes of the current slice, 16 rows each: <c>PlaneRows[plane * 16 + row]</c>
    /// bit <c>col</c>.</summary>
    internal readonly ushort[] PlaneRows = new ushort[Chunk.Size * Chunk.Size * Chunk.Size];

    /// <summary>One mesh build's 3x3x3 neighbourhood and its state arrays. The build never
    /// awaits, so sharing the thread's scratch instead of allocating two 27-element arrays
    /// per build is safe; slots are cleared once the light snapshot no longer needs them so
    /// pooled threads hold no chunk references at rest.</summary>
    internal readonly Chunk?[] Neighbors = new Chunk?[27];
    internal readonly byte[]?[] NeighborStates = new byte[27][];

    internal void ClearNeighbors()
    {
        Array.Clear(Neighbors, 0, Neighbors.Length);
        Array.Clear(NeighborStates, 0, NeighborStates.Length);
    }

    /// <summary>The 3x3 chunk-column terrain of the current build, in <c>dx * 3 + dz</c> order for
    /// column (cx - 1 + dx, cz - 1 + dz), resolved once by <see cref="ChunkLighting.FillColumns"/>
    /// from <c>Chunk.GenerateMeshData</c> (worldgen or streamed, through
    /// <see cref="ChunkManager.ColumnSource"/>). Read by the sun seeding,
    /// <see cref="ChunkMesher.SubmergedSurfaceCode"/> and the biome tint fields; cleared at the end
    /// of the build like <see cref="ClearNeighbors"/>, so pooled threads hold no column references
    /// at rest.</summary>
    internal readonly ColumnTerrain[] Columns = new ColumnTerrain[9];

    internal void ClearColumns() => Array.Clear(Columns, 0, Columns.Length);
}
