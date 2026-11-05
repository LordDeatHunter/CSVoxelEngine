using System.Numerics;

namespace VoxelEngine.Particles;

/// <summary>
/// One expanding ring on a water surface, GL-free. Born where a body crossed the surface with a
/// strength 0..1 (<see cref="WaterSplash.Strength"/>); its radius eases out to
/// <see cref="MaxRadius"/> over <see cref="Life"/> seconds and its opacity falls to nothing, both
/// as pure functions of the age so a test can read them without a window. Main thread only.
/// </summary>
public sealed class WaterRipple
{
    /// <summary>Where the ring is centered: the water top plus the lift, in world space.</summary>
    public Vector3 Position { get; }

    /// <summary>Seconds since the crossing.</summary>
    public float Age { get; private set; }

    /// <summary>Seconds the ring lives.</summary>
    public float Life { get; }

    /// <summary>Radius in blocks the ring reaches at the end of its life.</summary>
    public float MaxRadius { get; }

    public WaterRipple(Vector3 position, float strength, float sizeScale = 1f)
    {
        Position = position;
        Life = MathUtils.Lerp(ParticleConfig.RippleMinSeconds, ParticleConfig.RippleMaxSeconds, strength);
        MaxRadius = MathUtils.Lerp(ParticleConfig.RippleMinRadius, ParticleConfig.RippleMaxRadius, strength)
                    * sizeScale;
    }

    /// <summary>Ages the ring; false once it is over and should be removed.</summary>
    public bool Update(float deltaTime)
    {
        Age += deltaTime;
        return Age < Life;
    }

    /// <summary>Radius of the leading ring after <paramref name="age"/> seconds: fast at first, settling.</summary>
    public static float RadiusAt(float age, float life, float maxRadius) =>
        maxRadius * MathUtils.EaseOutCubic(age / life);

    /// <summary>Opacity share 0..1 after <paramref name="age"/> seconds: a quick fade-in over the first
    /// tenth of the life (no pop), then a fade to nothing that is gone exactly at the end.</summary>
    public static float OpacityAt(float age, float life)
    {
        var t = MathUtils.Clamp01(age / life);
        var fadeIn = MathUtils.SmoothStep(0f, 0.1f, t);
        var fadeOut = 1f - MathUtils.SmoothStep(0.15f, 1f, t);
        return fadeIn * fadeOut;
    }

    public float Radius => RadiusAt(Age, Life, MaxRadius);

    public float Opacity => OpacityAt(Age, Life);
}
