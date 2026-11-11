using System;
using System.Numerics;

namespace VoxelEngine.Particles;

/// <summary>
/// One ripple spreading over a water surface, GL-free. Born where a body crossed the surface with a
/// strength 0..1 (<see cref="WaterSplash.Strength"/>); its leading edge runs out to
/// <see cref="MaxRadius"/> over <see cref="Life"/> seconds while the wave train behind it
/// (<see cref="RippleWave"/>) thins with the spread and fades to nothing, all as pure functions of
/// the age so a test can read them without a window. Main thread only.
/// </summary>
public sealed class WaterRipple
{
    /// <summary>Where the body crossed: the water top of that column, in world space.</summary>
    public Vector3 Position { get; }

    /// <summary>Seconds since the crossing.</summary>
    public float Age { get; private set; }

    /// <summary>Seconds the ripple lives.</summary>
    public float Life { get; }

    /// <summary>Radius in blocks the leading edge reaches at the end of its life.</summary>
    public float MaxRadius { get; }

    /// <summary>Distance in blocks between two crests at the leading edge: shorter for a small body.</summary>
    public float Wavelength { get; }

    /// <summary>Peak slope of the packet when it is born, before the spread and the life thin it.</summary>
    public float PeakSlope { get; }

    /// <summary>0..1, fixed for life: turns the ring's slight irregularity and shifts its crests, so no
    /// two look alike.</summary>
    public float Seed { get; }

    public WaterRipple(Vector3 position, float strength, float sizeScale = 1f, float seed = 0f)
    {
        Position = position;
        Seed = seed;
        Life = MathUtils.Lerp(ParticleConfig.RippleMinSeconds, ParticleConfig.RippleMaxSeconds, strength);
        MaxRadius = MathUtils.Lerp(ParticleConfig.RippleMinRadius, ParticleConfig.RippleMaxRadius, strength)
                    * sizeScale;
        Wavelength = ParticleConfig.RippleWavelength * MathF.Sqrt(sizeScale);
        PeakSlope = MathUtils.Lerp(ParticleConfig.RippleMinSlope, ParticleConfig.RippleMaxSlope, strength);
    }

    /// <summary>Ages the ripple; false once it is over and should be removed.</summary>
    public bool Update(float deltaTime)
    {
        Age += deltaTime;
        return Age < Life;
    }

    /// <summary>Radius of the leading edge after <paramref name="age"/> seconds: a steady run that only
    /// settles a little toward the end (<see cref="ParticleConfig.RippleSteadyShare"/>).</summary>
    public static float RadiusAt(float age, float life, float maxRadius)
    {
        var t = MathUtils.Clamp01(age / life);
        return maxRadius * MathUtils.Lerp(MathUtils.EaseOutCubic(t), t, ParticleConfig.RippleSteadyShare);
    }

    /// <summary>Opacity share 0..1 after <paramref name="age"/> seconds: a quick fade-in over the first
    /// tenth of the life (no pop), then a fade to nothing that is gone exactly at the end.</summary>
    public static float OpacityAt(float age, float life)
    {
        var t = MathUtils.Clamp01(age / life);
        var fadeIn = MathUtils.SmoothStep(0f, 0.1f, t);
        var fadeOut = 1f - MathUtils.SmoothStep(0.4f, 1f, t);
        return fadeIn * fadeOut;
    }

    /// <summary>Share 0..1 of its slope a packet keeps with its leading edge at <paramref name="radius"/>
    /// blocks: all of it up to <see cref="ParticleConfig.RippleFullSlopeRadius"/>, then falling with the
    /// square root of the spread.</summary>
    public static float SpreadShare(float radius) =>
        MathF.Sqrt(ParticleConfig.RippleFullSlopeRadius / MathF.Max(radius, ParticleConfig.RippleFullSlopeRadius));

    public float Radius => RadiusAt(Age, Life, MaxRadius);

    /// <summary>Peak slope of the packet right now: the birth slope thinned by the spread and the life.</summary>
    public float Slope => PeakSlope * SpreadShare(Radius) * OpacityAt(Age, Life);

    /// <summary>The train's phase in cycles (<see cref="RippleWave.Slope"/>): it advances with the age, which
    /// runs the crests forward through the packet.</summary>
    public float Phase => Age * ParticleConfig.RippleCrestRate + Seed;
}
