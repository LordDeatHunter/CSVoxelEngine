using System.Runtime.CompilerServices;

namespace VoxelEngine.Chunks;

/// <summary>
/// Packs a world BLOCK cell into one <see cref="long"/> for the in-memory membership sets: the
/// fluid tick's and the leaf decay's schedules and the tree growth engine's cell sets. The bits
/// are <see cref="ChunkKey"/>'s 21-bit-per-axis packing; only the scale differs, block cells
/// here and chunk positions there, kept apart by name. Pure and static.
/// </summary>
internal static class CellKey
{
    /// <summary>Packed key for world block cell (x, y, z).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Pack(int x, int y, int z) => ChunkKey.Pack(x, y, z);

    /// <summary>World block cell a packed key came from.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Unpack(long key, out int x, out int y, out int z) => ChunkKey.Unpack(key, out x, out y, out z);
}
