using System;

namespace VoxelEngine.Particles;

/// <summary>
/// The shape of one splash ripple, GL-free twin of <c>RippleEnvelope</c>/<c>RippleSlopeAt</c> in
/// test-fragment.glsl (the formula, not the bits: GPU <c>sin</c> differs): the surface slope along the
/// way out from the crossing, as a function of how many wavelengths a point lies behind the leading
/// edge. A short train of waves trails that edge: calm water ahead of it, the packet rising just
/// behind it and dying away toward the middle, which is calm again once the train has passed. The
/// waves shorten toward the back (<see cref="ParticleConfig.RippleChirp"/>) and their crests run
/// forward through the packet as the phase advances. Unit amplitude: <see cref="WaterRipple.Slope"/>
/// scales it.
/// </summary>
public static class RippleWave
{
    /// <summary>Height of the packet 0..1 at <paramref name="wavesBehind"/> wavelengths behind the leading
    /// edge (negative = ahead of it): 0 outside the train.</summary>
    public static float Envelope(float wavesBehind)
    {
        if (wavesBehind <= -ParticleConfig.RippleLeadWaves || wavesBehind >= ParticleConfig.RippleTrainWaves)
            return 0f;
        var fall = MathF.Min(1f - wavesBehind / ParticleConfig.RippleTrainWaves, 1f);
        return MathUtils.SmoothStep(-ParticleConfig.RippleLeadWaves, ParticleConfig.RippleRiseWaves, wavesBehind)
               * fall * fall;
    }

    /// <summary>Surface slope -1..1 at <paramref name="wavesBehind"/> wavelengths behind the leading edge,
    /// with the train's <paramref name="phase"/> in cycles (it grows with the ripple's age, which is what
    /// carries the crests forward).</summary>
    public static float Slope(float wavesBehind, float phase)
    {
        var cycles = wavesBehind * (1f + ParticleConfig.RippleChirp * wavesBehind / ParticleConfig.RippleTrainWaves);
        return Envelope(wavesBehind) * MathF.Sin(MathF.Tau * (cycles + phase));
    }
}
