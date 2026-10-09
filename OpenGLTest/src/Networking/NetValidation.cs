using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The server's sanity rules for client input: a player state must be a plausible move from the
/// last one (not a teleport), and a block edit must target a cell within interaction reach of the
/// player's eye. GL-free and content-free, so the rules are tested directly.
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

    /// <summary>Whether a block cell's centre is within <paramref name="reach"/> blocks of the eye.</summary>
    public static bool InEditReach(Vector3 eye, int x, int y, int z, float reach)
    {
        var dx = x + 0.5f - eye.X;
        var dy = y + 0.5f - eye.Y;
        var dz = z + 0.5f - eye.Z;
        return dx * dx + dy * dy + dz * dz <= reach * reach;
    }
}
