using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Simulation.Fluids;

/// <summary>The fluid simulation's 21-bit-per-axis world-cell (and chunk) key packer, and the chunks a cell's write touches. Pure and static.</summary>
internal static class FluidCellKey
{
    // Cell keys pack a world position into 21 bits per axis (±1,048,576 blocks), which is far
    // beyond anything the chunk streamer will ever load.
    private const int PackBits = 21;
    private const long PackMask = (1L << PackBits) - 1;

    /// <summary>Adds the chunk owning a cell, plus the face-adjacent chunk on every axis where
    /// the cell sits on a chunk border, to <paramref name="into"/> as packed chunk keys.</summary>
    internal static void AddTouchedChunks(int x, int y, int z, HashSet<long> into)
    {
        var cx = x >> Chunk.SizeShift;
        var cy = y >> Chunk.SizeShift;
        var cz = z >> Chunk.SizeShift;
        into.Add(Pack(cx, cy, cz));

        var lx = x & Chunk.SizeMask;
        var ly = y & Chunk.SizeMask;
        var lz = z & Chunk.SizeMask;

        if (lx == 0) into.Add(Pack(cx - 1, cy, cz));
        else if (lx == Chunk.SizeMask) into.Add(Pack(cx + 1, cy, cz));

        if (ly == 0) into.Add(Pack(cx, cy - 1, cz));
        else if (ly == Chunk.SizeMask) into.Add(Pack(cx, cy + 1, cz));

        if (lz == 0) into.Add(Pack(cx, cy, cz - 1));
        else if (lz == Chunk.SizeMask) into.Add(Pack(cx, cy, cz + 1));
    }

    internal static Vector3 ChunkPosOf(int x, int y, int z) =>
        new(x >> Chunk.SizeShift, y >> Chunk.SizeShift, z >> Chunk.SizeShift);

    internal static long Pack(int x, int y, int z) =>
        ((x & PackMask) << (2 * PackBits)) | ((y & PackMask) << PackBits) | (z & PackMask);

    internal static void Unpack(long key, out int x, out int y, out int z)
    {
        x = SignExtend((int)((key >> (2 * PackBits)) & PackMask));
        y = UnpackedY(key);
        z = SignExtend((int)(key & PackMask));
    }

    // A packed key's Y, for ordering an over-budget tick lowest-cell-first without a full unpack
    private static int UnpackedY(long key) => SignExtend((int)((key >> PackBits) & PackMask));

    // A packed axis keeps only the low PackBits, so its sign bit has to be pushed back up
    private static int SignExtend(int value) => (value << (32 - PackBits)) >> (32 - PackBits);
}
