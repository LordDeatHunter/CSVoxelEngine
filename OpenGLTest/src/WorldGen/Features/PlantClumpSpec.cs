using System;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// The shape of one clumped plant scatter for <see cref="PlantClumps"/>: at most one clump per
/// square cell of <see cref="CellSize"/> blocks, accepted with <see cref="Chance"/>, its centre
/// jittered up to <see cref="Jitter"/> blocks off the cell's middle on each axis, and its members
/// inside a disc of <see cref="Radius"/> around that centre. The <see cref="MinMembers"/> cells
/// nearest the centre are always members (so every clump has at least that many), and the rest
/// pass a roll whose density falls linearly from 1 at the centre to <see cref="EdgeDensity"/> at
/// the rim. Immutable, so a generator builds it once and the worker threads share it.
/// </summary>
public sealed class PlantClumpSpec
{
    /// <summary>Validates the shape and settles the guaranteed core.</summary>
    /// <param name="cellShift">log2 of the cell edge in blocks.</param>
    /// <param name="radius">Member disc radius in blocks: a member's offset satisfies dx² + dz² ≤ r².</param>
    /// <param name="jitter">Largest offset of a centre from its cell's middle on each axis.</param>
    /// <param name="minMembers">How many of the cells nearest the centre always belong.</param>
    /// <param name="chance">Chance a cell holds a clump at all.</param>
    /// <param name="edgeDensity">Member density at the rim (1 at the centre).</param>
    /// <param name="salt">Separates this scatter's hashes from every other generator's.</param>
    public PlantClumpSpec(int cellShift, int radius, int jitter, int minMembers, float chance,
        float edgeDensity, int salt)
    {
        if (cellShift is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(cellShift), cellShift, "cell shift must be 1..16");
        var cellSize = 1 << cellShift;
        if (radius < 0)
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "radius must not be negative");
        // The centre must stay inside its own cell, which is what keeps every centre in a distinct cell
        if (jitter < 0 || 2 * jitter >= cellSize)
            throw new ArgumentOutOfRangeException(nameof(jitter), jitter, $"jitter must be 0..{cellSize / 2 - 1}");
        var discCells = CountDisc(radius * radius, radius);
        if (minMembers < 1 || minMembers > discCells)
            throw new ArgumentOutOfRangeException(nameof(minMembers), minMembers, $"min members must be 1..{discCells}");
        if (chance is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(chance), chance, "chance must be 0..1");
        if (edgeDensity is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(edgeDensity), edgeDensity, "edge density must be 0..1");

        CellShift = cellShift;
        Radius = radius;
        Jitter = jitter;
        MinMembers = minMembers;
        Chance = chance;
        EdgeDensity = edgeDensity;
        Salt = salt;

        // The smallest squared distance whose disc already holds MinMembers lattice cells: every
        // offset at or inside it is a guaranteed member. Rings are whole, so the core can hold a
        // few more than MinMembers (5 for 4, say), never fewer.
        var k = 0;
        while (CountDisc(k, radius) < minMembers) k++;
        CoreRadiusSquared = k;
    }

    /// <summary>log2 of <see cref="CellSize"/>.</summary>
    public int CellShift { get; }

    /// <summary>Edge of one clump cell in blocks.</summary>
    public int CellSize => 1 << CellShift;

    /// <summary>Member disc radius in blocks (Euclidean, inclusive).</summary>
    public int Radius { get; }

    /// <summary>Largest offset of a clump centre from its cell's middle, per axis.</summary>
    public int Jitter { get; }

    /// <summary>The fewest members any clump has, before terrain rejects any of them.</summary>
    public int MinMembers { get; }

    /// <summary>Chance a cell holds a clump.</summary>
    public float Chance { get; }

    /// <summary>Member density at the rim of the disc; the centre is always 1.</summary>
    public float EdgeDensity { get; }

    /// <summary>Hash salt of this scatter.</summary>
    public int Salt { get; }

    /// <summary>Squared distance from the centre within which every cell is a member.</summary>
    public int CoreRadiusSquared { get; }

    /// <summary>
    /// The real lower bound on the spacing of two clump centres, per axis on the axis their cells
    /// differ in (so also the Chebyshev and Euclidean bound): two cells' middles are a cell apart and
    /// each centre may move <see cref="Jitter"/> toward the other.
    /// </summary>
    public int MinCentreSpacing => CellSize - 2 * Jitter;

    /// <summary>Lattice cells (dx, dz) with |dx|, |dz| ≤ <paramref name="radius"/> and dx² + dz² ≤ <paramref name="radiusSquared"/>.</summary>
    private static int CountDisc(int radiusSquared, int radius)
    {
        var count = 0;
        for (var dx = -radius; dx <= radius; dx++)
        for (var dz = -radius; dz <= radius; dz++)
            if (dx * dx + dz * dz <= radiusSquared) count++;
        return count;
    }
}
