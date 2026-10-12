using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Extension operations on <see cref="Vector3"/> that are repeated across gameplay, physics
/// and render code. System.Numerics types cannot gain new operator overloads, so everything
/// here is an extension method with the same behavior the call sites previously inlined.
/// </summary>
public static class Vector3Utils
{
    /// <summary>Default length-squared guard for safe-normalize (billboards, flows, knockback).</summary>
    public const float NormalizeEpsilonSquared = 1e-6f;

    public static Vector3 Floor(this Vector3 vector3) => new(MathF.Floor(vector3.X), MathF.Floor(vector3.Y), MathF.Floor(vector3.Z));

    public static Vector3 Ceil(this Vector3 vector3) => new(MathF.Ceiling(vector3.X), MathF.Ceiling(vector3.Y), MathF.Ceiling(vector3.Z));

    public static Vector3 Round(this Vector3 vector3) => new(MathF.Round(vector3.X), MathF.Round(vector3.Y), MathF.Round(vector3.Z));

    public static Vector3 Sign(this Vector3 vector3) => new(MathF.Sign(vector3.X), MathF.Sign(vector3.Y), MathF.Sign(vector3.Z));

    /// <summary>Component-wise absolute value.</summary>
    public static Vector3 Abs(this Vector3 v) => new(MathF.Abs(v.X), MathF.Abs(v.Y), MathF.Abs(v.Z));

    /// <summary>Component-wise scale (Hadamard product): entity-model box mapping.</summary>
    public static Vector3 Scale(this Vector3 v, Vector3 s) => new(v.X * s.X, v.Y * s.Y, v.Z * s.Z);

    /// <summary>Drop the Y component (horizontal movement, knockback, felling axes).</summary>
    public static Vector3 FlattenY(this Vector3 v) => new(v.X, 0f, v.Z);

    /// <summary>Replace the Y component, keeping X/Z.</summary>
    public static Vector3 WithY(this Vector3 v, float y) => new(v.X, y, v.Z);

    /// <summary>True when the vector is shorter than <paramref name="epsilon"/> (degenerate input guard).</summary>
    public static bool IsNearZero(this Vector3 v, float epsilon = 1e-3f) =>
        v.LengthSquared() < epsilon * epsilon;

    /// <summary>
    /// Normalize, falling back to <paramref name="fallback"/> for degenerate input.
    /// Replaces the inlined LengthSquared-ternary repeated by every billboard, flow and
    /// knockback call site; <paramref name="minLengthSquared"/> is the squared-length guard,
    /// so existing literals (1e-6f, 1e-8f, 0f) carry over verbatim.
    /// </summary>
    public static Vector3 NormalizeSafeOr(this Vector3 v, Vector3 fallback, float minLengthSquared = NormalizeEpsilonSquared) =>
        v.LengthSquared() > minLengthSquared ? Vector3.Normalize(v) : fallback;

    /// <summary>Normalize, falling back to zero for degenerate input (fluid flow).</summary>
    public static Vector3 NormalizeSafe(this Vector3 v, float minLengthSquared = NormalizeEpsilonSquared) =>
        v.NormalizeSafeOr(Vector3.Zero, minLengthSquared);

    /// <summary>Flatten to XZ and normalize, falling back to <paramref name="fallback"/> when vertical.</summary>
    public static Vector3 NormalizeHorizontalOr(this Vector3 v, Vector3 fallback, float minLengthSquared = NormalizeEpsilonSquared) =>
        new Vector3(v.X, 0f, v.Z).NormalizeSafeOr(fallback, minLengthSquared);

    /// <summary>Clamped lerp between two positions (RenderManager.Mix, animation blends).</summary>
    public static Vector3 Lerp(this Vector3 a, Vector3 b, float t) =>
        a + (b - a) * MathUtils.Clamp01(t);

    /// <summary>Lerp without clamping, for deliberate overshoot.</summary>
    public static Vector3 LerpUnclamped(this Vector3 a, Vector3 b, float t) => a + (b - a) * t;

    /// <summary>Frame-rate independent easing of a position toward a target (one Damp per axis).</summary>
    public static Vector3 Damp(this Vector3 current, Vector3 target, float rate, float deltaTime) => new(
        MathUtils.Damp(current.X, target.X, rate, deltaTime),
        MathUtils.Damp(current.Y, target.Y, rate, deltaTime),
        MathUtils.Damp(current.Z, target.Z, rate, deltaTime));

    /// <summary>Block cell this position sits in, floored per axis (correct for negatives).</summary>
    public static (int X, int Y, int Z) BlockCell(this Vector3 v) =>
        ((int)MathF.Floor(v.X), (int)MathF.Floor(v.Y), (int)MathF.Floor(v.Z));

    /// <summary>View direction for a yaw/pitch look in degrees (yaw 0 faces +X, pitch clamps like mouse look).</summary>
    public static Vector3 FromYawPitch(float yawDegrees, float pitchDegrees)
    {
        var yaw = MathUtils.ToRadians(yawDegrees);
        var pitch = MathUtils.ToRadians(pitchDegrees);
        return Vector3.Normalize(new Vector3(
            MathF.Cos(pitch) * MathF.Cos(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Sin(yaw)));
    }

    /// <summary>Horizontal facing for a yaw in degrees.</summary>
    public static Vector3 FromYaw(float yawDegrees)
    {
        var yaw = MathUtils.ToRadians(yawDegrees);
        return Vector3.Normalize(new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw)));
    }

    /// <summary>Yaw in degrees for a horizontal direction (matches FromYaw).</summary>
    public static float YawDegrees(this Vector3 direction) =>
        MathUtils.ToDegrees(MathF.Atan2(direction.Z, direction.X));

    /// <summary>
    /// Camera-facing basis for point sprites: forward/right/up with the same degenerate
    /// fallbacks every particle renderer inlines (-Z forward, +X right).
    /// </summary>
    public static void BillboardBasis(this Vector3 forward, out Vector3 right, out Vector3 up)
    {
        var f = forward.NormalizeSafeOr(-Vector3.UnitZ);
        right = Vector3.Cross(f, Vector3.UnitY).NormalizeSafeOr(Vector3.UnitX);
        up = Vector3.Cross(right, f);
    }
}
