using System;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Clumped plant placement, the shared rule behind any plant that grows in clusters rather than
/// one stem every so many blocks (<see cref="CottonGenerator"/> is the first user). A pure
/// function of (x, z), the seed and a <see cref="PlantClumpSpec"/>, the way the tree cells are
/// (<see cref="TreeGenerator"/>), so every chunk agrees without shared state: each cell hashes
/// whether it holds a clump and where its jittered center sits, and a column asks every cell whose
/// clump could reach it, so a clump near a cell border is never cut in half.
/// </summary>
public static class PlantClumps
{
    // One hash per cell or column feeds several independent draws, each mixed from its own step
    // of the SplitMix64 sequence (the way SplitMix64.NextULong advances)
    private const ulong Golden = 0x9E3779B97F4A7C15UL;
    private const ulong AcceptStream = Golden;
    private const ulong JitterXStream = unchecked(Golden * 2);
    private const ulong JitterZStream = unchecked(Golden * 3);
    private const ulong MemberStream = unchecked(Golden * 4);

    /// <summary>
    /// Whether cell (<paramref name="cellX"/>, <paramref name="cellZ"/>) holds a clump, and the
    /// clump's center. The center always lies inside the cell (the spec caps the jitter), so
    /// centers of distinct clumps sit in distinct cells, at least
    /// <see cref="PlantClumpSpec.MinCenterSpacing"/> apart.
    /// </summary>
    public static bool TryGetClump(PlantClumpSpec spec, int seed, int cellX, int cellZ,
        out int centerX, out int centerZ)
    {
        var h = SplitMix64.Hash(cellX, cellZ, seed ^ spec.Salt);
        if (Unit(SplitMix64.Mix(h + AcceptStream)) >= spec.Chance)
        {
            centerX = 0;
            centerZ = 0;
            return false;
        }

        var span = (ulong)(2 * spec.Jitter + 1);
        var half = spec.CellSize >> 1;
        centerX = (cellX << spec.CellShift) + half + (int)(SplitMix64.Mix(h + JitterXStream) % span) - spec.Jitter;
        centerZ = (cellZ << spec.CellShift) + half + (int)(SplitMix64.Mix(h + JitterZStream) % span) - spec.Jitter;
        return true;
    }

    /// <summary>
    /// Whether column (<paramref name="x"/>, <paramref name="z"/>) belongs to the clump centered at
    /// (<paramref name="centerX"/>, <paramref name="centerZ"/>): inside the radius, and either in the
    /// guaranteed core or through the falloff roll (a hash of the column itself, so the answer
    /// never depends on who asks).
    /// </summary>
    public static bool IsMember(PlantClumpSpec spec, int seed, int centerX, int centerZ, int x, int z)
    {
        var dx = x - centerX;
        var dz = z - centerZ;
        // Box test first: it keeps the squares below from overflowing for far columns
        if (Math.Abs(dx) > spec.Radius || Math.Abs(dz) > spec.Radius) return false;
        var d2 = dx * dx + dz * dz;
        if (d2 > spec.Radius * spec.Radius) return false;
        if (d2 <= spec.CoreRadiusSquared) return true;

        // Dense at the center, thinning toward the rim. Radius > 0 here: a zero radius puts the
        // only cell (the center) in the core.
        var density = MathUtils.Lerp(1f, spec.EdgeDensity, MathF.Sqrt(d2) / spec.Radius);
        var h = SplitMix64.Hash(x, z, seed ^ spec.Salt);
        return Unit(SplitMix64.Mix(h + MemberStream)) < density;
    }

    /// <summary>
    /// The inclusive range of cells along one axis whose clumps can reach coordinate
    /// <paramref name="coord"/>: a center sits within <see cref="PlantClumpSpec.Jitter"/> of its
    /// cell's middle and a member within <see cref="PlantClumpSpec.Radius"/> of its center. Empty
    /// (<paramref name="cellMin"/> &gt; <paramref name="cellMax"/>) where no clump can ever reach.
    /// </summary>
    public static void ReachingCells(PlantClumpSpec spec, int coord, out int cellMin, out int cellMax)
    {
        var reach = spec.Jitter + spec.Radius;
        var half = spec.CellSize >> 1;
        // ceil((coord - half - reach) / size) and floor((coord - half + reach) / size); the shift
        // floors negatives too
        cellMin = (coord - half - reach + spec.CellSize - 1) >> spec.CellShift;
        cellMax = (coord - half + reach) >> spec.CellShift;
    }

    /// <summary>
    /// Whether column (<paramref name="x"/>, <paramref name="z"/>) is a member of any clump whose
    /// center passes <paramref name="centerGate"/> (a biome test, say; null admits every center),
    /// and that clump's center. Every cell that could reach the column is asked, in a fixed order,
    /// so a clump straddling a cell border has the same members from both sides; where two clumps
    /// of a loose spec overlap, the first in that order is reported. The gate runs only for a
    /// clump the column is already a member of, so a costly test stays rare.
    /// </summary>
    public static bool TryGetMember(PlantClumpSpec spec, int seed, int x, int z,
        Func<int, int, bool>? centerGate, out int centerX, out int centerZ)
    {
        ReachingCells(spec, x, out var cx0, out var cx1);
        ReachingCells(spec, z, out var cz0, out var cz1);        for (var cellX = cx0; cellX <= cx1; cellX++)
        for (var cellZ = cz0; cellZ <= cz1; cellZ++)
        {
            if (!TryGetClump(spec, seed, cellX, cellZ, out centerX, out centerZ)) continue;
            if (!IsMember(spec, seed, centerX, centerZ, x, z)) continue;
            if (centerGate != null && !centerGate(centerX, centerZ)) continue;
            return true;
        }

        centerX = 0;
        centerZ = 0;
        return false;
    }

    /// <summary>A hash's top 24 bits as a uniform float in [0, 1).</summary>
    private static float Unit(ulong h) => (h >> 40) * (1f / (1 << 24));
}
