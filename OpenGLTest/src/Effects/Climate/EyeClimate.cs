using System;
using VoxelEngine.Chunks;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Effects.Climate;

/// <summary>
/// GL-free climate at an arbitrary world column, read from an <see cref="IColumnSource"/>'s stored
/// corner climate (the biome-tint lattice, <see cref="ColumnTerrain.Warmth"/> and
/// <see cref="ColumnTerrain.Wetness"/>) rather than from worldgen noise, so a multiplayer client
/// (whose columns stream from the server) never evaluates <see cref="TerrainGenerator"/>. The
/// camera effects keyed to the eye's climate (the aurora's coldness, the biome color grade) read it.
/// </summary>
public static class EyeClimate
{
    /// <summary>
    /// The climate at world column (<paramref name="x"/>, <paramref name="z"/>): bilinear over the four
    /// lattice corners of the chunk column holding it, the same weights <see cref="BiomeTint"/>'s
    /// fields use. False when any of the four corner columns is not available (a streamed column
    /// that has not arrived); nothing is guessed in its place.
    /// </summary>
    public static bool TryAt(IColumnSource source, float x, float z, out float warmth, out float wetness)
    {
        var cx = (int)MathF.Floor(x) >> Chunk.SizeShift;
        var cz = (int)MathF.Floor(z) >> Chunk.SizeShift;
        if (!source.TryGet(cx, cz, out var c00)
            || !source.TryGet(cx + 1, cz, out var c10)
            || !source.TryGet(cx, cz + 1, out var c01)
            || !source.TryGet(cx + 1, cz + 1, out var c11))
        {
            warmth = 0.5f;
            wetness = 0.5f;
            return false;
        }

        var fx = MathUtils.Clamp01((x - cx * Chunk.Size) / Chunk.Size);
        var fz = MathUtils.Clamp01((z - cz * Chunk.Size) / Chunk.Size);
        warmth = Bilinear(c00.Warmth, c10.Warmth, c01.Warmth, c11.Warmth, fx, fz);
        wetness = Bilinear(c00.Wetness, c10.Wetness, c01.Wetness, c11.Wetness, fx, fz);
        return true;
    }

    /// <summary>How cold a climate warmth is, 0..1: the cold ramp (<see cref="TerrainGenerator.ColdWeight"/>)
    /// read back out of the warmth axis, which is 0 fully cold and exactly 0.5 across the temperate band.</summary>
    public static float Coldness(float warmth) => MathUtils.Clamp01(1f - 2f * warmth);

    /// <summary>How warm a climate warmth is, 0..1: the warm ramp (<see cref="TerrainGenerator.WarmWeight"/>)
    /// read back the same way, 0 through the temperate band and 1 fully warm.</summary>
    public static float Warmness(float warmth) => MathUtils.Clamp01(2f * warmth - 1f);

    private static float Bilinear(float c00, float c10, float c01, float c11, float fx, float fz) =>
        c00 * ((1f - fx) * (1f - fz)) + c10 * (fx * (1f - fz)) + c01 * ((1f - fx) * fz) + c11 * (fx * fz);
}
