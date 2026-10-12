using System;
using System.Numerics;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Effects.Lighting;

/// <summary>
/// GL-free flicker curves for the warm fire lights. Every campfire used to share one global
/// flicker, so a row of fires pulsed in lockstep; each now takes a phase hashed from its own
/// cell, which desynchronizes them while keeping any one fire's flicker stable frame to frame.
/// The shader's block-light flicker (<c>BlockFlicker</c> in test-fragment.glsl) does the same
/// per lattice corner on the GPU.
/// </summary>
public static class LightFlicker
{
    /// <summary>
    /// A phase in [0, Tau) that is a pure function of the integer cell containing
    /// <paramref name="cell"/>, so every point inside one block shares it.
    /// </summary>
    public static float CellPhase(Vector3 cell)
    {
        var x = (int)MathF.Floor(cell.X);
        var y = (int)MathF.Floor(cell.Y);
        var z = (int)MathF.Floor(cell.Z);
        // 21 bits per axis, like the block-scale cell packers elsewhere
        var packed = ((ulong)(uint)x & 0x1FFFFF)
                     | (((ulong)(uint)y & 0x1FFFFF) << 21)
                     | (((ulong)(uint)z & 0x1FFFFF) << 42);
        var top24 = SplitMix64.Mix(packed) >> 40;
        return top24 / (float)(1 << 24) * MathF.Tau;
    }

    /// <summary>The campfire point light's brightness multiplier at <paramref name="time"/>.</summary>
    public static float Campfire(float time, float phase) =>
        0.9f + 0.25f * MathF.Sin(time * 11f + phase) * MathF.Sin(time * 5.3f + 1.7f + phase * 1.7f);
}
