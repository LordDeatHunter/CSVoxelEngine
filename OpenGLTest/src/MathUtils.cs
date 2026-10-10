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

    /// <summary>Slow start (quadratic): the lower half of the range barely moves.</summary>
    public static float EaseInQuad(float t)
    {
        t = Clamp01(t);
        return t * t;
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

    /// <summary>Smooth start and finish (cubic), the symmetric counterpart of the one-sided cubics.</summary>
    public static float EaseInOutCubic(float t)
    {
        t = Clamp01(t);
        return t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f;
    }

    /// <summary>Overshoots 1 then settles back (anticipation-free "pop"); 0 at 0 and exactly 1 at 1.</summary>
    public static float EaseOutBack(float t, float overshoot = 1.70158f)
    {
        t = Clamp01(t) - 1f;
        return 1f + t * t * ((overshoot + 1f) * t + overshoot);
    }

    /// <summary>Springy ring-out around 1; 0 at 0 and exactly 1 at 1.</summary>
    public static float EaseOutElastic(float t)
    {
        t = Clamp01(t);
        if (t <= 0f || t >= 1f) return t;
        const float c4 = MathF.Tau / 3f;
        return MathF.Pow(2f, -10f * t) * MathF.Sin((t * 10f - 0.75f) * c4) + 1f;
    }

    /// <summary>Ball-drop bounces settling at 1; 0 at 0 and exactly 1 at 1.</summary>
    public static float EaseOutBounce(float t)
    {
        t = Clamp01(t);
        const float n1 = 7.5625f, d1 = 2.75f;
        if (t < 1f / d1) return n1 * t * t;
        if (t < 2f / d1) { t -= 1.5f / d1; return n1 * t * t + 0.75f; }
        if (t < 2.5f / d1) { t -= 2.25f / d1; return n1 * t * t + 0.9375f; }
        t -= 2.625f / d1;
        return n1 * t * t + 0.984375f;
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

    /// <summary>
    /// A repeating 0..1 beat, one every <paramref name="period"/> seconds: each cycle is a
    /// <see cref="Pulse"/> (a quick eased rise to 1 at <paramref name="peakAt"/> of the cycle,
    /// then a smooth fall back to 0), so it reads as a slow heartbeat rather than a sine wobble,
    /// and it is continuous across cycles. A non-positive period beats never (0).
    /// </summary>
    public static float Heartbeat(float time, float period, float peakAt = 0.3f)
    {
        if (period <= 0f) return 0f;
        var cycles = time / period;
        return Pulse(cycles - MathF.Floor(cycles), peakAt);
    }

    /// <summary>
    /// One-shot 0 -> 1 -> 0 bump with a springy settle, for a scale pop: an eased-out rise to 1
    /// at <paramref name="peakAt"/>, then an <see cref="EaseOutBack"/> fall that dips a little
    /// below 0 before landing on exactly 0 at t = 1, so whatever it scales rests where it began.
    /// <paramref name="peakAt"/> is clamped away from the ends like <see cref="Pulse"/>'s.
    /// </summary>
    public static float BackPulse(float t, float peakAt = 0.25f)
    {
        t = Clamp01(t);
        peakAt = MathF.Min(MathF.Max(peakAt, 0.05f), 0.95f);

        return t < peakAt
            ? EaseOutCubic(t / peakAt)
            : 1f - EaseOutBack((t - peakAt) / (1f - peakAt));
    }

    /// <summary>
    /// A timed label's opacity: fully on for <paramref name="holdSeconds"/> after it appeared,
    /// then a smooth fade to 0 over <paramref name="fadeSeconds"/>, and 0 from then on. Never
    /// increases with <paramref name="elapsed"/>, so a label only ever fades out.
    /// </summary>
    public static float HoldThenFade(float elapsed, float holdSeconds, float fadeSeconds)
    {
        if (elapsed <= holdSeconds) return 1f;
        if (fadeSeconds <= 0f) return 0f;
        return 1f - SmoothStep((elapsed - holdSeconds) / fadeSeconds);
    }
}
