namespace silkgltriangle;

/// <summary>
/// Every tree of one 64x64 XZ cell (<see cref="WorldGenerationConfig.TreeCellBlocks"/>), grown
/// together in placement-hash order in one <see cref="VirtualTreeSpace"/>: over the terrain, with
/// the cells of the trees already grown in this batch and the neighbour cells' SOLO shapes
/// (<see cref="TreeGenerator.ShapeOf"/>) as obstacles. Immutable after build and shared across
/// threads; a pure function of the cell key and the world seed, never of which cell was asked
/// for first. The one residual overlap inside a batch is a tree's BASE cell, which the grower
/// does not check (<see cref="TreeGrower.GrowToMaturity(TreeSpecies, int, int, int, ITreeSpace)"/>),
/// so it may coincide with an earlier tree's leaf (or, rarely, wood) cell; Stamp settles it
/// first-come, wood over leaves.
/// </summary>
internal sealed class TreeBatch
{
    /// <summary>The cell's trees in placement-hash order (hash, then x, then z).</summary>
    public required TreeGenerator.TreeShape[] Trees { get; init; }

    /// <summary>Lowest cell Y over every tree; <see cref="int.MaxValue"/> when the batch is empty.</summary>
    public required int MinY { get; init; }

    /// <summary>Highest cell Y over every tree; <see cref="int.MinValue"/> when the batch is empty.</summary>
    public required int MaxY { get; init; }
}
