using System;
using System.Numerics;
using VoxelEngine.Player;

namespace VoxelEngine.Entities.Goblins;

/// <summary>
/// GL-free ballistic aim for a goblin's thrown stone: the launch velocity of fixed speed that
/// lands a projectile under constant gravity on a target point. Callers pass
/// <see cref="ThrownProjectile.Gravity"/>, the gravity the stone actually flies under, so the
/// solve and the flight never disagree.
/// </summary>
public static class GoblinAim
{
    /// <summary>Below this horizontal distance the target is treated as straight above or below.</summary>
    private const float VerticalThrowDistance = 1e-4f;

    /// <summary>
    /// Solves the launch velocity (length <paramref name="speed"/>) from <paramref name="from"/>
    /// to <paramref name="to"/> under <paramref name="gravity"/>, taking the low arc when two
    /// exist. Returns false when the target is out of reach at that speed; the velocity is then
    /// the 45-degree maximum-range toss toward the target, so a distant player is still lobbed at.
    /// </summary>
    public static bool TryLaunchVelocity(Vector3 from, Vector3 to, float speed, float gravity, out Vector3 velocity)
    {
        var delta = to - from;
        var horizontal = new Vector3(delta.X, 0f, delta.Z);
        float d = horizontal.Length();
        float h = delta.Y;

        if (d < VerticalThrowDistance)
        {
            // Straight up or down: only the sign of the height matters.
            velocity = Vector3.UnitY * (h >= 0f ? speed : -speed);
            return true;
        }

        var unit = horizontal / d;
        float s2 = speed * speed;
        float disc = s2 * s2 - gravity * (gravity * d * d + 2f * h * s2);
        if (disc < 0f)
        {
            // Out of range: the steepest-reaching toss still heads for the player.
            float c = MathF.Sqrt(0.5f);
            velocity = unit * (speed * c) + Vector3.UnitY * (speed * c);
            return false;
        }

        // Low arc: tan(theta) = (v^2 - sqrt(disc)) / (g * d).
        float theta = MathF.Atan((s2 - MathF.Sqrt(disc)) / (gravity * d));
        velocity = unit * (speed * MathF.Cos(theta)) + Vector3.UnitY * (speed * MathF.Sin(theta));
        return true;
    }

    /// <summary>
    /// <see cref="TryLaunchVelocity"/> at where a moving target will be when the projectile arrives: the
    /// target's horizontal velocity carries it for the wind-up still to run plus the flight time, which is
    /// found by fixed-point iteration (the flight time depends on the aim point and vice versa). A target
    /// moving vertically (a jump or a fall) is led along its parabola under the player's gravity, never
    /// below where it was aimed for a jump (it lands at its start height) or <see cref="MaxLeadDrop"/> below for a
    /// fall, since the ground is not known here.
    /// </summary>
    public static bool TryLeadVelocity(Vector3 from, Vector3 to, Vector3 targetVelocity, float speed, float gravity,
        float windupSeconds, out Vector3 velocity)
    {
        float flight = 0f;
        bool reachable = TryLaunchVelocity(from, to, speed, gravity, out velocity);
        for (int i = 0; i < LeadIterations; i++)
        {
            var horizontalSpeed = new Vector3(velocity.X, 0f, velocity.Z).Length();
            var d = new Vector3(to.X - from.X, 0f, to.Z - from.Z).Length();
            flight = horizontalSpeed > 1e-3f ? MathF.Min(d / horizontalSpeed, MaxLeadFlightSeconds) : 0f;
            reachable = TryLaunchVelocity(from, PredictTarget(to, targetVelocity, windupSeconds + flight), speed, gravity,
                out velocity);
        }

        return reachable;
    }

    /// <summary>Where a target at <paramref name="to"/> moving at <paramref name="velocity"/> is after
    /// <paramref name="seconds"/>: straight along the ground, on a gravity parabola when airborne.</summary>
    private static Vector3 PredictTarget(Vector3 to, Vector3 velocity, float seconds)
    {
        float y = 0f;
        if (MathF.Abs(velocity.Y) > AirborneSpeed)
            y = MathF.Max(velocity.Y * seconds - 0.5f * PlayerPhysics.Gravity * seconds * seconds,
                velocity.Y > 0f ? 0f : -MaxLeadDrop);
        return to + new Vector3(velocity.X * seconds, y, velocity.Z * seconds);
    }

    /// <summary>Vertical speed above which the target counts as airborne; a grounded body reports none.</summary>
    private const float AirborneSpeed = 0.5f;

    /// <summary>The furthest the lead drops below the locked aim point, in blocks.</summary>
    private const float MaxLeadDrop = 4f;

    /// <summary>Fixed-point passes of the lead solve; the flight time converges in two or three.</summary>
    private const int LeadIterations = 4;

    /// <summary>Caps the flight time the lead trusts, so a lob at a far target never leads it past reason.</summary>
    private const float MaxLeadFlightSeconds = 2f;
}
