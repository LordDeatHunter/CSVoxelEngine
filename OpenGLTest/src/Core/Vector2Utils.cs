using System;
using System.Numerics;

namespace VoxelEngine.Core;

/// <summary>
/// Extension operations on <see cref="Vector2"/> mirroring <see cref="Vector3Utils"/>.
/// Covers the UI/menu-screen math that repeats component-wise Floor/Lerp/Damp handling.
/// </summary>
public static class Vector2Utils
{
    public static Vector2 Floor(this Vector2 v) => new(MathF.Floor(v.X), MathF.Floor(v.Y));

    public static Vector2 Ceil(this Vector2 v) => new(MathF.Ceiling(v.X), MathF.Ceiling(v.Y));

    public static Vector2 Round(this Vector2 v) => new(MathF.Round(v.X), MathF.Round(v.Y));

    /// <summary>Clamped lerp between two points (UI slides, menu transitions).</summary>
    public static Vector2 Lerp(this Vector2 a, Vector2 b, float t) =>
        a + (b - a) * MathUtils.Clamp01(t);

    /// <summary>Frame-rate independent easing of a point toward a target (one Damp per axis).</summary>
    public static Vector2 Damp(this Vector2 current, Vector2 target, float rate, float deltaTime) => new(
        MathUtils.Damp(current.X, target.X, rate, deltaTime),
        MathUtils.Damp(current.Y, target.Y, rate, deltaTime));
}
