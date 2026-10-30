using System;
using System.Numerics;
using VoxelEngine.Player;

namespace VoxelEngine.Entities.Icebergs;

/// <summary>
/// How a player body meets an iceberg's boxes (world-space (Min, Max) pairs): overlap, riding a
/// box's top, and side contact with the direction the body pushes the berg. Also the one
/// definition of the player body box (<see cref="BodyMin"/>, <see cref="BodyMax"/>), which
/// <see cref="BodyCollision"/> builds its collision box from. GL-free and state-free.
/// </summary>
public static class IcebergContact
{
    // Touching faces are not an overlap, and a hair of float drift is not a gap.
    private const float Epsilon = 1e-4f;

    // How far below a box's top the feet may sit and still ride it (float drift of a resting body).
    private const float RideSink = 1e-3f;

    /// <summary>The lower corner of the player body whose eye is at <paramref name="eye"/>.</summary>
    public static Vector3 BodyMin(Vector3 eye) => new(
        eye.X - PlayerPhysics.PlayerRadius,
        eye.Y - Camera.EyeHeight,
        eye.Z - PlayerPhysics.PlayerRadius);

    /// <summary>The upper corner of the player body whose eye is at <paramref name="eye"/>.</summary>
    public static Vector3 BodyMax(Vector3 eye) => new(
        eye.X + PlayerPhysics.PlayerRadius,
        eye.Y - Camera.EyeHeight + PlayerPhysics.PlayerHeight,
        eye.Z + PlayerPhysics.PlayerRadius);

    /// <summary>Whether two boxes overlap on all three axes; faces that only touch do not.</summary>
    public static bool Overlaps(Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax) =>
        OverlapOn(aMin.X, aMax.X, bMin.X, bMax.X) > Epsilon
        && OverlapOn(aMin.Y, aMax.Y, bMin.Y, bMax.Y) > Epsilon
        && OverlapOn(aMin.Z, aMax.Z, bMin.Z, bMax.Z) > Epsilon;

    /// <summary>
    /// Whether the body stands on the box's top: it overlaps the box horizontally and its feet are
    /// at the top, up to <see cref="IcebergConfig.RideSlackBlocks"/> above it.
    /// </summary>
    public static bool IsRiding(Vector3 bodyMin, Vector3 bodyMax, Vector3 boxMin, Vector3 boxMax) =>
        OverlapOn(bodyMin.X, bodyMax.X, boxMin.X, boxMax.X) > Epsilon
        && OverlapOn(bodyMin.Z, bodyMax.Z, boxMin.Z, boxMax.Z) > Epsilon
        && bodyMin.Y >= boxMax.Y - RideSink
        && bodyMin.Y <= boxMax.Y + IcebergConfig.RideSlackBlocks;

    /// <summary>
    /// Whether the body leans against a side of the box: they overlap vertically, the body does not
    /// ride the box, and on exactly one horizontal axis the gap between them is at most
    /// <see cref="IcebergConfig.ContactSlackBlocks"/> while they overlap on the other.
    /// <paramref name="pushDirection"/> is then the unit (x, z) axis from the body toward the box.
    /// </summary>
    public static bool TrySideContact(Vector3 bodyMin, Vector3 bodyMax, Vector3 boxMin, Vector3 boxMax,
        out Vector2 pushDirection)
    {
        pushDirection = Vector2.Zero;
        if (OverlapOn(bodyMin.Y, bodyMax.Y, boxMin.Y, boxMax.Y) <= Epsilon) return false;
        if (IsRiding(bodyMin, bodyMax, boxMin, boxMax)) return false;

        var overlapX = OverlapOn(bodyMin.X, bodyMax.X, boxMin.X, boxMax.X);
        var overlapZ = OverlapOn(bodyMin.Z, bodyMax.Z, boxMin.Z, boxMax.Z);
        // A negative overlap is the gap; touching (0) or a hair inside still counts as contact.
        var contactX = overlapX <= Epsilon && -overlapX <= IcebergConfig.ContactSlackBlocks;
        var contactZ = overlapZ <= Epsilon && -overlapZ <= IcebergConfig.ContactSlackBlocks;

        if (contactX && overlapZ > Epsilon)
        {
            pushDirection = new Vector2(Toward(bodyMin.X, bodyMax.X, boxMin.X, boxMax.X), 0f);
            return true;
        }

        if (contactZ && overlapX > Epsilon)
        {
            pushDirection = new Vector2(0f, Toward(bodyMin.Z, bodyMax.Z, boxMin.Z, boxMax.Z));
            return true;
        }

        return false;
    }

    // The overlap of two intervals on one axis: positive inside, negative the gap between them.
    private static float OverlapOn(float aMin, float aMax, float bMin, float bMax) =>
        MathF.Min(aMax, bMax) - MathF.Max(aMin, bMin);

    // +1 when the box lies on the positive side of the body along this axis, else -1.
    private static float Toward(float bodyMin, float bodyMax, float boxMin, float boxMax) =>
        boxMin + boxMax >= bodyMin + bodyMax ? 1f : -1f;
}
