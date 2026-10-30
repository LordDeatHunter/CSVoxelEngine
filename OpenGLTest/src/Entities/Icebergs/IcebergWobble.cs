using System;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Entities.Icebergs;

/// <summary>
/// The render-only wobble of a floating berg: a bob and a small pitch and roll, each two sines of
/// periods and phases hashed from the berg's seed, so neighboring bergs never wobble in lockstep.
/// A pure function of (seed, time, scale); nothing of it reaches collision or the wire. A bigger
/// berg wobbles less (<see cref="IcebergConfig.WobbleScaleExponent"/>). GL-free and state-free.
/// </summary>
public static class IcebergWobble
{
    // Keeps the wobble's hashes apart from any other per-seed hash (the drift angle's).
    private const ulong WobbleSalt = 0x3C6E_F372_FE94_F82BUL;

    private const double TwoPi = Math.PI * 2.0;

    /// <summary>
    /// The berg's bob in blocks and its pitch and roll in radians at <paramref name="time"/>
    /// seconds: within <see cref="IcebergConfig.BobBlocks"/> and
    /// <see cref="IcebergConfig.TiltRadians"/>, divided by <paramref name="scale"/> to
    /// <see cref="IcebergConfig.WobbleScaleExponent"/>.
    /// </summary>
    public static (float Bob, float Pitch, float Roll) Sample(int seed, double time, float scale)
    {
        var damping = MathF.Pow(MathF.Max(scale, 1e-3f), IcebergConfig.WobbleScaleExponent);
        return (
            IcebergConfig.BobBlocks / damping * Channel(seed, 0, time),
            IcebergConfig.TiltRadians / damping * Channel(seed, 1, time),
            IcebergConfig.TiltRadians / damping * Channel(seed, 2, time));
    }

    // One channel: the mean of two sines, so it stays within -1..1.
    private static float Channel(int seed, int channel, double time) =>
        0.5f * (Wave(seed, channel * 2, time) + Wave(seed, channel * 2 + 1, time));

    // One sine with a hashed period in [WobblePeriodMinSeconds, WobblePeriodMaxSeconds] and a hashed
    // phase. The phase is worked out in double and wrapped before the cast, so a long-running clock
    // never loses the float precision that would make the wave step.
    private static float Wave(int seed, int wave, double time)
    {
        var period = IcebergConfig.WobblePeriodMinSeconds
            + (IcebergConfig.WobblePeriodMaxSeconds - IcebergConfig.WobblePeriodMinSeconds) * Unit(seed, wave * 2);
        var phase = time * TwoPi / period + Unit(seed, wave * 2 + 1) * TwoPi;
        phase %= TwoPi;
        return MathF.Sin((float)phase);
    }

    // A hashed value in 0..1 for the k-th draw of this seed.
    private static double Unit(int seed, int k)
    {
        var hash = SplitMix64.Mix(((ulong)(uint)seed ^ WobbleSalt) + (ulong)k * 0x9E37_79B9_7F4A_7C15UL);
        return (hash >> 40) / (double)(1UL << 24);
    }
}
