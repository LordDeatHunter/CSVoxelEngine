using System;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Simulation;

/// <summary>
/// The world as a felled tree feels it, by cell. <see cref="Stops"/>: what the falling wood rests
/// on. <see cref="Snags"/>: what it passes through but a branch moving fast through it breaks on.
/// <see cref="HoldsPin"/>: what the cut can stand on, so a tree keeps tipping over its stump or the
/// trunk left standing below it.
/// </summary>
public sealed record FellTerrain(
    Func<int, int, int, bool> Stops,
    Func<int, int, int, bool> Snags,
    Func<int, int, int, bool> HoldsPin)
{
    /// <summary>Plain ground with nothing to snag on: what stops the tree also holds its pin.</summary>
    public static FellTerrain Ground(Func<int, int, int, bool> isSolid) => new(isSolid, (_, _, _) => false, isSolid);

    /// <summary>
    /// The live world. An unloaded cell is ground, so nothing falls into it. A standing tree's
    /// leaves and its thin wood (branches, a thin trunk) do not hold up the heavy falling wood: it
    /// crashes through a neighbor's crown, where the branches snag it, instead of being lifted onto
    /// them (a crown wedged in the tangle used to climb out of it and launch). Full-thickness trunks
    /// and player-placed wood still stop it.
    /// </summary>
    public static FellTerrain Of(ChunkManager chunks) => new(
        (x, y, z) => !chunks.TryGetCell(x, y, z, out var id, out var state)
                     || (BlockType.IsSolid(id) && !TreeGenerator.IsLeaf(id) && !IsStandingThinWood(chunks, x, y, z, id, state)),
        (x, y, z) => chunks.TryGetCell(x, y, z, out var id, out var state) && IsStandingThinWood(chunks, x, y, z, id, state),
        (x, y, z) => !chunks.TryGetCell(x, y, z, out var id, out _) || (BlockType.IsSolid(id) && !TreeGenerator.IsLeaf(id)));

    private static bool IsStandingThinWood(ChunkManager chunks, int x, int y, int z, ushort id, byte state) =>
        BlockType.IsTreeWood(id) && !WoodState.IsFullCube(state) && !chunks.IsPlayerPlaced(x, y, z);
}
