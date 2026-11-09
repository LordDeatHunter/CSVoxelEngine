namespace VoxelEngine.WorldGen.Features.Underground;

/// <summary>
/// What one <see cref="OreNoiseLattice.Stamp{TRule}"/> caller replaces: which cells may host the
/// deposit and what each replaced cell becomes. Implemented by a struct, so the lattice loop is
/// specialized per generator with no delegate and no allocation.
/// </summary>
internal interface IOreLatticeRule
{
    /// <summary>
    /// Whether the chunk-local cell (localX, localY, localZ) holding <paramref name="block"/> may be
    /// replaced by the deposit.
    /// </summary>
    bool IsHost(byte block, int localX, int localY, int localZ);

    /// <summary>The block a host cell at world (worldX, worldY, worldZ) inside the deposit becomes.</summary>
    byte Replacement(int worldX, int worldY, int worldZ);

    /// <summary>
    /// What the chunk-local cell (localX, localY, localZ) adds to the stamp's threshold (the cave biome
    /// richness, plans/CAVE_BIOMES.md CB-2): negative lowers it, so the deposit is richer there; 0 keeps
    /// the plain field. Read only for host cells. No default implementation: a default interface member
    /// would box the struct rule on its constrained call.
    /// </summary>
    float ThresholdShift(int localX, int localY, int localZ);

    /// <summary>
    /// A lower bound of every cell's <see cref="ThresholdShift"/>, so a lattice cell whose corners all lie
    /// at or below the threshold plus it holds nothing and is skipped unread.
    /// </summary>
    float MinThresholdShift { get; }
}
