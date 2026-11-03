using System;
using System.Runtime.CompilerServices;
using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen.Features.Underground;

/// <summary>
/// The shared coarse-lattice sampler of the ore vein and gravel/sand pocket noise. A cell's value
/// is the trilinear blend, at the cell's center, of the noise at the 8 lattice points around it;
/// lattice points sit on world multiples of <see cref="Stride"/>, so neighboring chunks read the
/// same samples and agree at every seam. One more rule is part of the field's definition: a lattice
/// cell whose 8 corners are all at or below the threshold holds nothing (a trilinear blend never
/// exceeds its largest corner), which is what lets <see cref="Stamp{TRule}"/> skip whole cells.
/// <see cref="Stamp{TRule}"/> and the point query <see cref="Exceeds"/> apply that rule and call the
/// one <see cref="Trilinear"/> on the same integer lattice coordinates and the same fraction
/// expressions, so they agree bit for bit. A pure function of world position and the noise, and
/// thread-safe: the noise is read-only after its reseed, and all scratch lives on the stack.
/// </summary>
internal static class OreNoiseLattice
{
    /// <summary>Lattice spacing in blocks (<see cref="WorldGenerationConfig.OreNoiseStride"/>).</summary>
    public const int Stride = WorldGenerationConfig.OreNoiseStride;

    private const int Mask = Stride - 1;
    private const int Cells = Chunk.Size / Stride;   // lattice cells per chunk axis
    private const int Grid = Cells + 1;              // lattice points per chunk axis, both faces included
    private const int GridSq = Grid * Grid;
    private const int GridCount = GridSq * Grid;
    private const int CellCount = Cells * Cells * Cells;
    private const float InvStride = 1f / Stride;

    static OreNoiseLattice() => ValidateStride(Stride, Chunk.Size);

    /// <summary>Throws unless <paramref name="stride"/> is a positive power of two dividing the chunk size.</summary>
    private static void ValidateStride(int stride, int chunkSize)
    {
        if (stride > 0 && (stride & (stride - 1)) == 0 && chunkSize % stride == 0) return;

        throw new InvalidOperationException(
            $"OreNoiseStride {stride} must be a positive power of two dividing the chunk size {chunkSize}.");
    }

    /// <summary>
    /// Whether the field of <paramref name="noise"/> at world cell (x, y, z) exceeds
    /// <paramref name="threshold"/>: exactly what <see cref="Stamp{TRule}"/> decides for that cell.
    /// </summary>
    public static bool Exceeds(FastNoiseLite noise, int x, int y, int z, float threshold)
    {
        // Two's complement masking floors negatives too, so the base is the lattice point at or below
        var bx = x & ~Mask;
        var by = y & ~Mask;
        var bz = z & ~Mask;
        var c000 = noise.GetNoise(bx, by, bz);
        var c001 = noise.GetNoise(bx, by, bz + Stride);
        var c010 = noise.GetNoise(bx, by + Stride, bz);
        var c011 = noise.GetNoise(bx, by + Stride, bz + Stride);
        var c100 = noise.GetNoise(bx + Stride, by, bz);
        var c101 = noise.GetNoise(bx + Stride, by, bz + Stride);
        var c110 = noise.GetNoise(bx + Stride, by + Stride, bz);
        var c111 = noise.GetNoise(bx + Stride, by + Stride, bz + Stride);
        if (!MayExceed(c000, c001, c010, c011, c100, c101, c110, c111, threshold)) return false;

        var fx = ((x & Mask) + 0.5f) * InvStride;
        var fy = ((y & Mask) + 0.5f) * InvStride;
        var fz = ((z & Mask) + 0.5f) * InvStride;
        return Trilinear(c000, c001, c010, c011, c100, c101, c110, c111, fx, fy, fz) > threshold;
    }

    /// <summary>
    /// Replaces every host cell of the chunk at world origin (worldX0, worldY0, worldZ0) whose field
    /// exceeds <paramref name="threshold"/> with <paramref name="rule"/>'s replacement. Lattice cells
    /// holding no host are never sampled, and lattice points are sampled lazily, once each.
    /// </summary>
    public static void Stamp<TRule>(byte[] blocks, FastNoiseLite noise, float threshold,
        int worldX0, int worldY0, int worldZ0, TRule rule) where TRule : struct, IOreLatticeRule
    {
        Span<bool> hostCells = stackalloc bool[CellCount];
        var anyHost = false;
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            if (!rule.IsHost(blocks[Chunk.Idx(x, y, z)], x, y, z)) continue;

            hostCells[(x / Stride) * Cells * Cells + (y / Stride) * Cells + z / Stride] = true;
            anyHost = true;
        }

        if (!anyHost) return;

        // NaN marks a lattice point not sampled yet; the noise never returns NaN
        Span<float> samples = stackalloc float[GridCount];
        samples.Fill(float.NaN);

        for (var cx = 0; cx < Cells; cx++)
        for (var cy = 0; cy < Cells; cy++)
        for (var cz = 0; cz < Cells; cz++)
        {
            if (!hostCells[cx * Cells * Cells + cy * Cells + cz]) continue;

            StampCell(blocks, noise, threshold, worldX0, worldY0, worldZ0, rule, samples, cx, cy, cz);
        }
    }

    /// <summary>Stamps the host cells of one lattice cell (cx, cy, cz) of a chunk.</summary>
    private static void StampCell<TRule>(byte[] blocks, FastNoiseLite noise, float threshold,
        int worldX0, int worldY0, int worldZ0, TRule rule, Span<float> samples, int cx, int cy, int cz)
        where TRule : struct, IOreLatticeRule
    {
        var c000 = Corner(samples, noise, worldX0, worldY0, worldZ0, cx, cy, cz);
        var c001 = Corner(samples, noise, worldX0, worldY0, worldZ0, cx, cy, cz + 1);
        var c010 = Corner(samples, noise, worldX0, worldY0, worldZ0, cx, cy + 1, cz);
        var c011 = Corner(samples, noise, worldX0, worldY0, worldZ0, cx, cy + 1, cz + 1);
        var c100 = Corner(samples, noise, worldX0, worldY0, worldZ0, cx + 1, cy, cz);
        var c101 = Corner(samples, noise, worldX0, worldY0, worldZ0, cx + 1, cy, cz + 1);
        var c110 = Corner(samples, noise, worldX0, worldY0, worldZ0, cx + 1, cy + 1, cz);
        var c111 = Corner(samples, noise, worldX0, worldY0, worldZ0, cx + 1, cy + 1, cz + 1);
        if (!MayExceed(c000, c001, c010, c011, c100, c101, c110, c111, threshold)) return;

        var x0 = cx * Stride;
        var y0 = cy * Stride;
        var z0 = cz * Stride;
        for (var x = x0; x < x0 + Stride; x++)
        for (var y = y0; y < y0 + Stride; y++)
        for (var z = z0; z < z0 + Stride; z++)
        {
            var i = Chunk.Idx(x, y, z);
            if (!rule.IsHost(blocks[i], x, y, z)) continue;

            // The same fraction expression as Exceeds: the chunk origin is a lattice multiple
            var fx = ((x & Mask) + 0.5f) * InvStride;
            var fy = ((y & Mask) + 0.5f) * InvStride;
            var fz = ((z & Mask) + 0.5f) * InvStride;
            if (Trilinear(c000, c001, c010, c011, c100, c101, c110, c111, fx, fy, fz) <= threshold) continue;

            blocks[i] = rule.Replacement(worldX0 + x, worldY0 + y, worldZ0 + z);
        }
    }

    /// <summary>
    /// The noise at lattice point (gx, gy, gz) of the chunk at world origin (worldX0, worldY0, worldZ0),
    /// sampled into <paramref name="samples"/> the first time it is asked for.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Corner(Span<float> samples, FastNoiseLite noise, int worldX0, int worldY0, int worldZ0,
        int gx, int gy, int gz)
    {
        ref var slot = ref samples[gx * GridSq + gy * Grid + gz];
        if (float.IsNaN(slot))
            slot = noise.GetNoise(worldX0 + gx * Stride, worldY0 + gy * Stride, worldZ0 + gz * Stride);
        return slot;
    }

    /// <summary>Whether the largest of the 8 corners is above the threshold.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MayExceed(float c000, float c001, float c010, float c011,
        float c100, float c101, float c110, float c111, float threshold)
    {
        var max = MathF.Max(MathF.Max(MathF.Max(c000, c001), MathF.Max(c010, c011)),
            MathF.Max(MathF.Max(c100, c101), MathF.Max(c110, c111)));
        return max > threshold;
    }

    /// <summary>
    /// Trilinear blend of the corners cXYZ at fraction (fx, fy, fz): lerped in z, then y, then x,
    /// the order <see cref="NoiseCaves"/> uses.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Trilinear(float c000, float c001, float c010, float c011,
        float c100, float c101, float c110, float c111, float fx, float fy, float fz)
    {
        var x0y0 = c000 + (c001 - c000) * fz;
        var x0y1 = c010 + (c011 - c010) * fz;
        var x1y0 = c100 + (c101 - c100) * fz;
        var x1y1 = c110 + (c111 - c110) * fz;

        var x0 = x0y0 + (x0y1 - x0y0) * fy;
        var x1 = x1y0 + (x1y1 - x1y0) * fy;
        return x0 + (x1 - x0) * fx;
    }
}
