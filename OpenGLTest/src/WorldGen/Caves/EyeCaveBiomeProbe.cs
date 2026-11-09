using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Chunks.Sources;

namespace VoxelEngine.WorldGen.Caves;

/// <summary>
/// The cave biome at one player's eye (plans/CAVE_BIOMES.md CB-3), memoized per cell: <see cref="Update"/>
/// recomputes only when the eye enters another cell, at the cost of one column read
/// (<see cref="IWorldSource.GetColumn"/>, almost always cached since the eye's chunk is loaded) and the
/// three noise blends of <see cref="CaveBiomes.At"/>. <see cref="CaveBiomes.Classify"/> itself gives
/// <see cref="CaveBiome.Plain"/> above the column top, inside the echo cover and in the sky, and an eye at or
/// below the barrier is plain without reading a column. Runs only on an authoritative world (singleplayer
/// and the server, one per player, main thread), never on a client, which has no seed and whose streamed
/// <c>ColumnTerrain</c> carries no surface biome: a client takes the byte from its <c>PlayerStatus</c>.
/// </summary>
public sealed class EyeCaveBiomeProbe
{
    private int _x, _y, _z;
    private bool _hasCell;

    /// <summary>The cave biome at the eye as of the last <see cref="Update"/>; plain before the first.</summary>
    public CaveBiome Current { get; private set; }

    /// <summary>The cave biome at <paramref name="eye"/> over <paramref name="source"/>'s columns.</summary>
    public CaveBiome Update(IWorldSource source, Vector3 eye)
    {
        var x = (int)MathF.Floor(eye.X);
        var y = (int)MathF.Floor(eye.Y);
        var z = (int)MathF.Floor(eye.Z);
        if (_hasCell && x == _x && y == _y && z == _z) return Current;
        _x = x;
        _y = y;
        _z = z;
        _hasCell = true;

        if (y <= WorldGenerationConfig.BarrierTopY)
        {
            Current = CaveBiome.Plain;
            return Current;
        }

        // Arithmetic shifts and masks floor negatives, so (lx, lz) is the cell inside its chunk column
        var column = source.GetColumn(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
        var i = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
        Current = CaveBiomes.At(x, y, z, column.Biomes[i], column.Heights[i]);
        return Current;
    }
}
