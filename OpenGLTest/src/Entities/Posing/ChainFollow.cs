using System;
using System.Numerics;

namespace VoxelEngine.Entities.Posing;

/// <summary>
/// The follow-the-leader step of a body chain (the sandworm's segments): every point is
/// dragged to its link distance behind the one ahead, toward where it was, and no link may bend sharper than a
/// limit, so a hard turn sweeps the body round in an arc rather than folding it. GL-free and allocation-free; the
/// result depends only on the path the first point took.
/// </summary>
public static class ChainFollow
{
    /// <summary>
    /// Drags <paramref name="points"/> 1.. behind point 0, which the caller has already moved.
    /// <paramref name="links"/>[i] is the distance from point i to the one ahead of it (entry 0 is unused).
    /// A link bends at most <paramref name="maxBendRadians"/> against the link ahead of it; the first link has
    /// none ahead and is free.
    /// </summary>
    public static void Follow(Span<Vector3> points, ReadOnlySpan<float> links, float maxBendRadians)
    {
        for (var i = 1; i < points.Length; i++)
        {
            var parent = points[i - 1];
            var dir = Direction(points[i] - parent, Vector3.UnitZ);
            if (i >= 2)
                dir = LimitBend(dir, Direction(parent - points[i - 2], Vector3.UnitZ), maxBendRadians);
            points[i] = parent + dir * links[i];
        }
    }

    /// <summary><paramref name="v"/> as a unit vector, or <paramref name="fallback"/> when it has no length.</summary>
    public static Vector3 Direction(Vector3 v, Vector3 fallback)
    {
        var lengthSquared = v.LengthSquared();
        return lengthSquared < 1e-10f ? fallback : v / MathF.Sqrt(lengthSquared);
    }

    // The direction a link may take: dir itself, or the nearest direction to it within the bend limit of
    // `previous`, the direction of the link ahead of it (both point from the front of the chain toward its end).
    private static Vector3 LimitBend(Vector3 dir, Vector3 previous, float maxBendRadians)
    {
        var cos = Math.Clamp(Vector3.Dot(dir, previous), -1f, 1f);
        if (MathF.Acos(cos) <= maxBendRadians) return dir;
        var side = dir - previous * cos;
        if (side.LengthSquared() < 1e-8f) return previous;
        side = Vector3.Normalize(side);
        return previous * MathF.Cos(maxBendRadians) + side * MathF.Sin(maxBendRadians);
    }
}
