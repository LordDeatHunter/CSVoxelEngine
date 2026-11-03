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
}
