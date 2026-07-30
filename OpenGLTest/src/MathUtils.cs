using System;

namespace silkgltriangle;

/// <summary>
/// Shared scalar math: interpolation and easing curves for animations (mining pop, fades)
/// plus a few constants. Easing helpers all take and return normalized 0..1 values and
/// clamp their input, so callers can feed raw timers without pre-clamping.
/// </summary>
public static class MathUtils
{
    public const float ONE_THIRD = 1F / 3F;

    public static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

    /// <summary>Linear interpolation with t clamped to 0..1.</summary>
    public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

    /// <summary>Linear interpolation without clamping, for deliberate overshoot.</summary>
    public static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Where value sits between a and b as a 0..1 fraction; 0 for a degenerate range.</summary>
    public static float InverseLerp(float a, float b, float value) =>
        MathF.Abs(b - a) < 1e-6f ? 0f : Clamp01((value - a) / (b - a));

    /// <summary>Hermite ease in and out.</summary>
    public static float SmoothStep(float t)
    {
        t = Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Slow start, fast finish.</summary>
    public static float EaseInCubic(float t)
    {
        t = Clamp01(t);
        return t * t * t;
    }

    /// <summary>Fast start, slow finish — reads as something snapping into place.</summary>
    public static float EaseOutCubic(float t)
    {
        var inverse = 1f - Clamp01(t);
        return 1f - inverse * inverse * inverse;
    }

    /// <summary>
    /// One-shot 0 -> 1 -> 0 pulse across t in 0..1. The rise is short and eased-out and the
    /// fall is smooth, so it reads as an impact rather than a symmetric wobble.
    /// <paramref name="peakAt"/> is where the pulse tops out and is clamped away from the
    /// ends of the range.
    /// </summary>
    public static float Pulse(float t, float peakAt = 0.3f)
    {
        t = Clamp01(t);
        peakAt = MathF.Min(MathF.Max(peakAt, 0.05f), 0.95f);

        return t < peakAt
            ? EaseOutCubic(t / peakAt)
            : 1f - SmoothStep((t - peakAt) / (1f - peakAt));
    }
}
