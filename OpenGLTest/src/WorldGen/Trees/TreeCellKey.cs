using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen.Trees;

/// <summary>
/// Packs a block cell into one <see cref="long"/> for the growth engine's membership sets.
/// <see cref="ChunkKey.Pack(int, int, int)"/> is a scale-agnostic 21-bit-per-axis packer; here it
/// keys block cells, the same budget <c>FluidManager</c> uses for its cell keys.
/// </summary>
internal static class TreeCellKey
{
    /// <summary>Packed key of a block cell.</summary>
    internal static long Pack(int x, int y, int z) => ChunkKey.Pack(x, y, z);
}
