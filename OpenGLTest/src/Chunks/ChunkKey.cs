using System.Numerics;
using System.Runtime.CompilerServices;

namespace VoxelEngine.Chunks;

/// <summary>
/// Packs a chunk position into a single <see cref="long"/> so the chunk dictionaries can be
/// keyed on one integer hash instead of a three-float <see cref="Vector3"/> hash.
/// <para>
/// The 21-bits-per-axis budget is deliberately the same one <c>FluidManager</c> already commits
/// to for its cell keys — copying the constant rather than sizing a fresh one keeps the two
/// packers readable side by side and makes <c>SignExtend</c> a literal copy. The difference is
/// scale: FluidManager's keys are block-scale, these are chunk-scale.
/// </para>
/// <para>
/// Range, and note that it is ASYMMETRIC because 21 bits signed is not +/-2^20: the representable
/// chunk coordinates are -1,048,576..+1,048,575 (the positive edge is 2^20 - 1, not 2^20), which
/// is -16,777,216..+16,777,215 blocks — 16x beyond the range FluidManager's block keys reach, and
/// far past anything the chunk streamer will ever load. <c>Pack(2^20)</c> does not round-trip; it
/// wraps to -2^20, and that contract is pinned by a fact rather than left to be discovered.
/// </para>
/// <para>
/// Inputs are FLOORED, never truncated: <see cref="Pack(Vector3)"/> goes through
/// <see cref="MathUtils.FloorToInt"/> and <see cref="PackBlock"/> uses an arithmetic shift, both
/// matching <see cref="VoxelCoords.ChunkPosOf(int, int, int)"/>'s floor-division convention. A
/// truncating key would silently merge chunk -0 into chunk 0 on the negative side.
/// </para>
/// </summary>
internal static class ChunkKey
{
    internal const int PackBits = 21;
    internal const long PackMask = (1L << PackBits) - 1;

    /// <summary>Packed key for a chunk position given in chunk units.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Pack(int cx, int cy, int cz) =>
        ((cx & PackMask) << (2 * PackBits)) | ((cy & PackMask) << PackBits) | (cz & PackMask);

    /// <summary>Packed key for a chunk position held as a <see cref="Vector3"/>, floored per axis.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Pack(Vector3 chunkPos) =>
        Pack(MathUtils.FloorToInt(chunkPos.X), MathUtils.FloorToInt(chunkPos.Y), MathUtils.FloorToInt(chunkPos.Z));

    /// <summary>
    /// Packed key for the chunk holding a world BLOCK cell. The arithmetic shift floors for
    /// negatives, so this is exactly <see cref="VoxelCoords.ChunkPosOf(int, int, int)"/> packed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long PackBlock(int x, int y, int z) =>
        Pack(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);

    /// <summary>Chunk coordinates a packed key came from.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Unpack(long key, out int cx, out int cy, out int cz)
    {
        cx = SignExtend((int)((key >> (2 * PackBits)) & PackMask));
        cy = SignExtend((int)((key >> PackBits) & PackMask));
        cz = SignExtend((int)(key & PackMask));
    }

    /// <summary>Chunk position a packed key came from, as the <see cref="Vector3"/> the API still speaks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector3 ToVector3(long key)
    {
        Unpack(key, out var cx, out var cy, out var cz);
        return new Vector3(cx, cy, cz);
    }

    // A packed axis keeps only the low PackBits, so its sign bit has to be pushed back up
    private static int SignExtend(int value) => (value << (32 - PackBits)) >> (32 - PackBits);
}
