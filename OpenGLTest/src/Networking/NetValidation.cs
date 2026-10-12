using System;
using System.Numerics;

namespace VoxelEngine.Networking;

/// <summary>
/// The server's sanity rules for client input: a player state must be a plausible move from the
/// last one (not a teleport), a world intent must be aimed from (near) where the player is and
/// must name a cell within interaction reach of the player's eye, and a placement may only go
/// against a face the eye sees. GL-free and content-free, so the rules are tested directly.
/// </summary>
public static class NetValidation
{
    /// <summary>
    /// Accepts a move whose distance is within <paramref name="maxSpeed"/> blocks per second over
    /// <paramref name="deltaSeconds"/> plus a small margin (dropped packets, a laggy frame). A
    /// rejected move sets <paramref name="accepted"/> to <paramref name="from"/> moved toward
    /// <paramref name="to"/> by exactly the allowed distance, so the authoritative position
    /// converges on the client at the speed cap instead of freezing at a stale point (which would
    /// get every later state rejected too).
    /// </summary>
    public static bool AcceptMove(Vector3 from, Vector3 to, double deltaSeconds, float maxSpeed,
        out Vector3 accepted)
    {
        var distance = Vector3.Distance(from, to);
        var allowed = AllowedDistance(deltaSeconds, maxSpeed);
        if (distance <= allowed)
        {
            accepted = to;
            return true;
        }

        accepted = from + (to - from) * (allowed / distance);
        return false;
    }

    /// <summary>The distance <see cref="AcceptMove"/> allows over <paramref name="deltaSeconds"/>.</summary>
    public static float AllowedDistance(double deltaSeconds, float maxSpeed) =>
        maxSpeed * MathF.Max((float)deltaSeconds, 0.001f) + 2f;

    /// <summary>Whether a block cell's center is within <paramref name="reach"/> blocks of the eye.</summary>
    public static bool InEditReach(Vector3 eye, int x, int y, int z, float reach)
    {
        var dx = x + 0.5f - eye.X;
        var dy = y + 0.5f - eye.Y;
        var dz = z + 0.5f - eye.Z;
        return dx * dx + dy * dy + dz * dz <= reach * reach;
    }

    /// <summary>
    /// Whether the eye a world intent claims lies within <paramref name="maxDrift"/> blocks of the
    /// player's authoritative position. Beyond it the intent was aimed from somewhere the player is
    /// not, and is refused.
    /// </summary>
    public static bool EyeWithin(Vector3 authoritative, Vector3 claimed, float maxDrift) =>
        Vector3.DistanceSquared(authoritative, claimed) <= maxDrift * maxDrift;

    /// <summary>
    /// Whether a placement may go against the face of <paramref name="cell"/> whose outward normal
    /// is <paramref name="normal"/>, seen from <paramref name="eye"/>: the normal must be one of the
    /// six axis directions, and the eye must lie on its outer side of the cell's center. A client's
    /// third-person aim can report a different face of the hit cell than the server's first-person
    /// ray does, so the face is judged by visibility rather than by equality; a face that points
    /// away from the eye (the far side of a wall) is refused.
    /// </summary>
    public static bool FaceFacesEye(Vector3 cell, Vector3 normal, Vector3 eye)
    {
        var axes = MathF.Abs(normal.X) + MathF.Abs(normal.Y) + MathF.Abs(normal.Z);
        if (axes != 1f || (normal.X != 0f && MathF.Abs(normal.X) != 1f)
            || (normal.Y != 0f && MathF.Abs(normal.Y) != 1f) || (normal.Z != 0f && MathF.Abs(normal.Z) != 1f))
            return false;
        var center = cell + new Vector3(0.5f);
        return Vector3.Dot(eye - center, normal) > 0f;
    }
}
