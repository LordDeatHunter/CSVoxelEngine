using System;
using System.Collections.Generic;

namespace VoxelEngine.Tests.WorldGen.Features.Underground;

/// <summary>
/// Deposit statistics over a stamped volume, shared by the ore and pocket tests: the cells a
/// generator's chunk <c>Stamp</c> replaces across x 0..127, y -64..31, z 0..127 (whole chunks),
/// their 6-connected deposits, the mean deposit size and the lattice phase resultant.
/// </summary>
internal static class OreScan
{
    private const int MinX = 0;
    private const int MinY = -64;
    private const int MinZ = 0;
    private const int ChunksX = 8;
    private const int ChunksY = 6;
    private const int ChunksZ = 8;

    private static readonly (int x, int y, int z)[] Offsets =
    {
        (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1),
    };

    /// <summary>
    /// Every cell, in world coordinates, that became <paramref name="ore"/> when each chunk of
    /// the scan volume, filled with <paramref name="host"/>, went through <paramref name="stamp"/>
    /// (called with the buffer and the chunk's world origin).
    /// </summary>
    public static HashSet<(int x, int y, int z)> StampedCells(
        Action<ushort[], int, int, int> stamp, ushort host, ushort ore) =>
        StampedCells(stamp, host, block => block == ore);

    /// <summary>
    /// Every cell, in world coordinates, whose block satisfies <paramref name="became"/> after
    /// each chunk of the scan volume, filled with <paramref name="host"/>, went through
    /// <paramref name="stamp"/>.
    /// </summary>
    public static HashSet<(int x, int y, int z)> StampedCells(
        Action<ushort[], int, int, int> stamp, ushort host, Func<ushort, bool> became)
    {
        var cells = new HashSet<(int x, int y, int z)>();
        var blocks = new ushort[Chunk.Size * Chunk.Size * Chunk.Size];
        for (var cx = 0; cx < ChunksX; cx++)
        for (var cy = 0; cy < ChunksY; cy++)
        for (var cz = 0; cz < ChunksZ; cz++)
        {
            var x0 = MinX + cx * Chunk.Size;
            var y0 = MinY + cy * Chunk.Size;
            var z0 = MinZ + cz * Chunk.Size;
            Array.Fill(blocks, host);
            stamp(blocks, x0, y0, z0);
            CollectChunk(cells, blocks, x0, y0, z0, became);
        }
        return cells;
    }

    private static void CollectChunk(HashSet<(int x, int y, int z)> cells, ushort[] blocks,
        int x0, int y0, int z0, Func<ushort, bool> became)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            if (became(blocks[Chunk.Idx(x, y, z)])) cells.Add((x0 + x, y0 + y, z0 + z));
        }
    }

    /// <summary>The 6-connected components of <paramref name="cells"/>.</summary>
    public static List<List<(int x, int y, int z)>> Deposits(HashSet<(int x, int y, int z)> cells)
    {
        var deposits = new List<List<(int x, int y, int z)>>();
        var visited = new HashSet<(int x, int y, int z)>();
        var work = new Stack<(int x, int y, int z)>();
        foreach (var cell in cells)
        {
            if (!visited.Add(cell)) continue;
            var deposit = new List<(int x, int y, int z)>();
            work.Push(cell);
            while (work.Count > 0)
            {
                var c = work.Pop();
                deposit.Add(c);
                foreach (var d in Offsets)
                {
                    var n = (c.x + d.x, c.y + d.y, c.z + d.z);
                    if (cells.Contains(n) && visited.Add(n)) work.Push(n);
                }
            }
            deposits.Add(deposit);
        }
        return deposits;
    }

    /// <summary>The mean number of cells per deposit, 0 when there are none.</summary>
    public static double MeanSize(List<List<(int x, int y, int z)>> deposits)
    {
        if (deposits.Count == 0) return 0;
        var total = 0;
        foreach (var deposit in deposits) total += deposit.Count;
        return (double)total / deposits.Count;
    }

    /// <summary>
    /// How strongly the deposit centroids lock to a lattice of <paramref name="stride"/> along
    /// <paramref name="axis"/> (0 x, 1 y, 2 z): each centroid of cell centers (coordinate + 0.5)
    /// becomes a phase 2 pi ((c mod stride) / stride), and the result is the length of the mean
    /// unit phasor. Near 1 when every deposit centers on the same lattice offset, near
    /// 1 / sqrt(deposits) when they center freely.
    /// </summary>
    public static double PhaseResultant(List<List<(int x, int y, int z)>> deposits, int axis, int stride)
    {
        if (deposits.Count == 0) return 0;
        double sumCos = 0;
        double sumSin = 0;
        foreach (var deposit in deposits)
        {
            double sum = 0;
            foreach (var c in deposit) sum += Coordinate(c, axis) + 0.5;
            var centroid = sum / deposit.Count;
            var phase = 2 * Math.PI * (((centroid % stride) + stride) % stride) / stride;
            sumCos += Math.Cos(phase);
            sumSin += Math.Sin(phase);
        }
        return Math.Sqrt(sumCos * sumCos + sumSin * sumSin) / deposits.Count;
    }

    private static int Coordinate((int x, int y, int z) c, int axis) => axis switch
    {
        0 => c.x,
        1 => c.y,
        2 => c.z,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };
}
