using System.Runtime.CompilerServices;

namespace VoxelEngine.Chunks.Meshing;

/// <summary>
/// The packed per-face key the greedy mesher groups faces by: block id in bits 0-15, the four
/// 2-bit corner AO values in 16-23, the submerged flag in 24 and, for a submerged face, its 7-bit
/// water surface code in 25-31. The key stays an <see cref="int"/>; bit 31 can be set, which is
/// safe because the plane table's slot hash reads the key as <c>(uint)key</c> and an empty slot is
/// table entry 0, never a key value. The smooth light and the light tint travel in parallel keys
/// beside it (see <see cref="ChunkMesher"/>).
/// </summary>
internal static class GreedyKey
{
    public const int IdMask = 0xFFFF;
    public const int AoShift = 16;
    public const int AoMask = 0xFF;
    public const int SubmergedBit = 1 << 24;
    public const int SurfaceShift = 25;
    public const int SurfaceMask = 127;

    /// <summary>The key of a face of block <paramref name="id"/> with packed corner AO <paramref name="ao"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Pack(ushort id, int ao) => id | (ao & AoMask) << AoShift;

    /// <summary>Marks <paramref name="key"/> submerged and stores the face's water surface code.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WithSurface(int key, int surfaceCode) =>
        key | SubmergedBit | (surfaceCode & SurfaceMask) << SurfaceShift;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort IdOf(int key) => (ushort)(key & IdMask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AoOf(int key) => (key >> AoShift) & AoMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSubmerged(int key) => (key & SubmergedBit) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SurfaceCodeOf(int key) => (key >> SurfaceShift) & SurfaceMask;
}
