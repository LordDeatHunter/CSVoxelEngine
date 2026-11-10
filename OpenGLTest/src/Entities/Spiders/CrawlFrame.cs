using System;
using System.Numerics;

namespace VoxelEngine.Entities.Spiders;

/// <summary>
/// The frame a crawler stands in on one face of the block grid: the face's outward normal (one of the six axis
/// directions) and a heading on it, written as an angle so it fits a mob's <see cref="Mob.Facing"/> and the wire.
/// On a floor the angle is the usual yaw (the model faces -Z at 0); every other face has its own pair of axes the
/// angle turns in, chosen so the body stays right-handed. GL-free and state-free.
/// </summary>
public static class CrawlFrame
{
    /// <summary>The wire index of "holds no surface" (a falling or floating crawler).</summary>
    public const int Airborne = 0;

    // The six faces by wire index - 1, each with the two axes its heading angle turns in (A x B = -normal, as
    // +X x +Z = -Y on a floor)
    private static readonly Vector3[] Normals =
        [Vector3.UnitY, -Vector3.UnitY, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ];
    private static readonly Vector3[] AxisA =
        [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY, Vector3.UnitY, Vector3.UnitX];
    private static readonly Vector3[] AxisB =
        [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY];

    /// <summary>The wire index (1..6) of the face whose normal is nearest <paramref name="normal"/>.</summary>
    public static int IndexOf(Vector3 normal)
    {
        var best = 0;
        var bestDot = float.NegativeInfinity;
        for (var i = 0; i < Normals.Length; i++)
        {
            var dot = Vector3.Dot(Normals[i], normal);
            if (dot <= bestDot) continue;
            bestDot = dot;
            best = i;
        }
        return best + 1;
    }

    /// <summary>The normal of the face with wire index <paramref name="index"/>; zero for
    /// <see cref="Airborne"/> or an index out of range.</summary>
    public static Vector3 NormalOf(int index) =>
        index >= 1 && index <= Normals.Length ? Normals[index - 1] : Vector3.Zero;

    /// <summary>The unit heading at <paramref name="angle"/> radians on the face with <paramref name="normal"/>.</summary>
    public static Vector3 HeadingOf(Vector3 normal, float angle)
    {
        var face = IndexOf(normal) - 1;
        return -MathF.Sin(angle) * AxisA[face] - MathF.Cos(angle) * AxisB[face];
    }

    /// <summary>The angle of <paramref name="heading"/> on the face with <paramref name="normal"/>: the inverse of
    /// <see cref="HeadingOf"/> for a heading that lies in the face.</summary>
    public static float AngleOf(Vector3 normal, Vector3 heading)
    {
        var face = IndexOf(normal) - 1;
        return MathF.Atan2(-Vector3.Dot(heading, AxisA[face]), -Vector3.Dot(heading, AxisB[face]));
    }

    /// <summary>
    /// The rotation that stands a model (right +X, up +Y, front -Z) with its up along <paramref name="up"/> and its
    /// front along <paramref name="heading"/> as far as the up allows. A heading along the up falls back to any
    /// direction across it.
    /// </summary>
    public static Quaternion Orientation(Vector3 up, Vector3 heading)
    {
        up = Vector3.Normalize(up);
        var forward = heading - up * Vector3.Dot(heading, up);
        if (forward.LengthSquared() < 1e-6f)
        {
            var any = MathF.Abs(up.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitZ;
            forward = any - up * Vector3.Dot(any, up);
        }
        forward = Vector3.Normalize(forward);
        var right = Vector3.Cross(forward, up);
        // Row vectors: the model's +X, +Y and +Z (its back) land on these rows
        return Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            -forward.X, -forward.Y, -forward.Z, 0f,
            0f, 0f, 0f, 1f));
    }
}
