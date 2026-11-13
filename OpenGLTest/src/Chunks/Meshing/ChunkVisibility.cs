using System;
using static VoxelEngine.Chunks.Chunk;

namespace VoxelEngine.Chunks.Meshing;

/// <summary>
/// A chunk's face-to-face visibility set for the cave cull (<see cref="ChunkVisibilityGraph"/>): which pairs of its
/// six faces are joined by a run of open (non-occluding) cells inside it, so a line of sight that enters through one
/// face can leave through the other. Built at mesh time by a flood fill over the chunk's own cells; bit
/// <c>a * 6 + b</c> is set for every joined pair, both ways. Faces are numbered by axis and sign, -X, +X, -Y, +Y,
/// -Z, +Z, so a face's opposite is <c>face ^ 1</c>. GL-free; thread-safe (its scratch is per thread).
/// </summary>
public static class ChunkVisibility
{
    /// <summary>The face count, and the number of face directions the cave cull walks.</summary>
    public const int Faces = 6;

    /// <summary>Every face sees every other: an all-air chunk, or one whose cells are not known.</summary>
    public const ulong All = (1UL << (Faces * Faces)) - 1;

    /// <summary>Whether a line of sight through face <paramref name="from"/> can leave through <paramref name="to"/>.</summary>
    public static bool Connected(ulong set, int from, int to) => (set >> (from * Faces + to) & 1UL) != 0;

    /// <summary>The (x, y, z) step of face <paramref name="face"/>, pointing out of the chunk.</summary>
    public static (int X, int Y, int Z) Step(int face) => face switch
    {
        0 => (-1, 0, 0),
        1 => (1, 0, 0),
        2 => (0, -1, 0),
        3 => (0, 1, 0),
        4 => (0, 0, -1),
        _ => (0, 0, 1),
    };

    [ThreadStatic] private static bool[]? _visited;
    [ThreadStatic] private static int[]? _stack;

    /// <summary>
    /// The visibility set of <paramref name="blocks"/> (one chunk's cells in <see cref="Chunk.Idx"/> order): flood
    /// fills every component of open cells and joins the faces each one touches. A chunk with no open cell sees
    /// through nothing (0), an all-open one through everything.
    /// </summary>
    internal static ulong Compute(ChunkBlocks blocks)
    {
        var occludes = BlockType.OccludesById;
        var visited = _visited ??= new bool[BlockCount];
        var stack = _stack ??= new int[BlockCount];
        Array.Clear(visited);
        var set = 0UL;
        for (var start = 0; start < BlockCount; start++)
        {
            if (visited[start]) continue;
            visited[start] = true;
            if (occludes[blocks.Get(start)]) continue;
            set |= Join(FloodFaces(blocks, occludes, visited, stack, start));
            if (set == All) return All;
        }
        return set;
    }

    // The faces the open component holding start touches, as a 6-bit mask
    private static int FloodFaces(ChunkBlocks blocks, bool[] occludes, bool[] visited, int[] stack, int start)
    {
        var faces = 0;
        var top = 0;
        stack[top++] = start;
        while (top > 0)
        {
            var i = stack[--top];
            var x = i >> (2 * SizeShift);
            var y = (i >> SizeShift) & SizeMask;
            var z = i & SizeMask;
            faces |= Touches(x, 0) | Touches(y, 2) | Touches(z, 4);
            for (var face = 0; face < Faces; face++)
            {
                var (dx, dy, dz) = Step(face);
                var nx = x + dx;
                var ny = y + dy;
                var nz = z + dz;
                if ((uint)nx >= Size || (uint)ny >= Size || (uint)nz >= Size) continue;
                var n = Idx(nx, ny, nz);
                if (visited[n]) continue;
                visited[n] = true;
                if (!occludes[blocks.Get(n)]) stack[top++] = n;
            }
        }
        return faces;
    }

    // The low/high face bit of an axis whose first face is axisFace, for a cell at coordinate c
    private static int Touches(int c, int axisFace) =>
        c == 0 ? 1 << axisFace : c == Size - 1 ? 1 << (axisFace + 1) : 0;

    // Every pair of the faces in mask joined, both ways (a face also reaches itself: a pocket against one face)
    private static ulong Join(int mask)
    {
        var set = 0UL;
        for (var a = 0; a < Faces; a++)
        {
            if ((mask >> a & 1) == 0) continue;
            for (var b = 0; b < Faces; b++)
                if ((mask >> b & 1) != 0)
                    set |= 1UL << (a * Faces + b);
        }
        return set;
    }
}
