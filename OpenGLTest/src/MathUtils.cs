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

    /// <summary>Pitch clamp the mouse look and every headless look override share.</summary>
    public const float PitchMinDegrees = -89f;
    /// <summary>Pitch clamp the mouse look and every headless look override share.</summary>
    public const float PitchMaxDegrees = 89f;

    public static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

    /// <summary>Clamp a 0..1 fraction in double precision (time deltas, UI fractions).</summary>
    public static double Clamp01(double value) => value < 0.0 ? 0.0 : value > 1.0 ? 1.0 : value;

    /// <summary>Uniform clamp spelled the same way at every call site (float).</summary>
    public static float Clamp(float value, float min, float max) => Math.Clamp(value, min, max);

    /// <summary>Uniform clamp spelled the same way at every call site (int).</summary>
    public static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    /// <summary>Uniform clamp spelled the same way at every call site (double).</summary>
    public static double Clamp(double value, double min, double max) => Math.Clamp(value, min, max);

    /// <summary>Clamp a look pitch exactly like the mouse look does.</summary>
    public static float ClampPitch(float pitch) => Math.Clamp(pitch, PitchMinDegrees, PitchMaxDegrees);

    /// <summary>Degrees to radians (float).</summary>
    public static float ToRadians(float degrees) => degrees * (MathF.PI / 180f);

    /// <summary>Radians to degrees (float).</summary>
    public static float ToDegrees(float radians) => radians * (180f / MathF.PI);

    /// <summary>Cosine of an angle given in degrees.</summary>
    public static float CosDeg(float degrees) => MathF.Cos(ToRadians(degrees));

    /// <summary>Sine of an angle given in degrees.</summary>
    public static float SinDeg(float degrees) => MathF.Sin(ToRadians(degrees));

    /// <summary>Floor a float to the block/cell int it sits in (correct for negatives).</summary>
    public static int FloorToInt(float value) => (int)MathF.Floor(value);

    /// <summary>Floor a double to the block/cell int it sits in (correct for negatives).</summary>
    public static int FloorToInt(double value) => (int)Math.Floor(value);

    /// <summary>Snap a coordinate down to its grid field (cloud copies, LOD cells).</summary>
    public static float Snap(float coord, float field) => MathF.Floor(coord / field) * field;

    /// <summary>
    /// Floor division for chunk math: rounds toward negative infinity so negative world
    /// coordinates land in the chunk below zero instead of chunk zero.
    /// </summary>
    public static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    /// <summary>
    /// Positive remainder for chunk math: always in [0, b), so negative world coordinates
    /// wrap into the local cell instead of going negative.
    /// </summary>
    public static int Mod(int a, int b) => ((a % b) + b) % b;

    /// <summary>
    /// Clamp a per-axis collision remainder to one step (falling blocks, debris, drops,
    /// slimes, the player): the axis moves at most <paramref name="step"/> toward zero.
    /// </summary>
    public static float ClampStep(float remaining, float step) => Math.Clamp(remaining, -step, step);

    /// <summary>
    /// Frame-rate independent easing of a yaw angle toward a target, wrapping the delta
    /// into [-180, 180] first so the turn never spins the long way around.
    /// </summary>
    public static float DampAngle(float current, float target, float rate, float deltaTime)
    {
        var delta = target - current;
        delta -= MathF.Round(delta / 360f) * 360f;
        return current + delta * (1f - MathF.Exp(-rate * MathF.Max(0f, deltaTime)));
    }

    /// <summary>Linear interpolation with t clamped to 0..1.</summary>
    public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

    /// <summary>
    /// Frame-rate independent easing toward a target: the remaining distance decays
    /// exponentially at <paramref name="rate"/> per second, so the result of two half-steps
    /// equals one full step and a frame hitch never overshoots.
    /// </summary>
    public static float Damp(float current, float target, float rate, float deltaTime) =>
        target + (current - target) * MathF.Exp(-rate * MathF.Max(0f, deltaTime));

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

    /// <summary>GLSL-style smoothstep: 0 at or below edge0, 1 at or above edge1, Hermite between.</summary>
    public static float SmoothStep(float edge0, float edge1, float x) => SmoothStep(InverseLerp(edge0, edge1, x));

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
    /// Continuous -1..1 oscillation, completing one cycle every <paramref name="period"/>
    /// seconds. Starts at 0 and rises first, so an animation using it begins from rest.
    /// </summary>
    public static float Oscillate(float time, float period) =>
        period <= 0f ? 0f : MathF.Sin(time / period * MathF.Tau);

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
