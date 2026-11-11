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
    /// <see cref="TryLaunchVelocity"/> at where a moving target will be when the projectile arrives. The flight
    /// time and the aim point depend on each other, so the solve iterates: fly to the aim point, move the aim
    /// point to where the target is after that flight, and again. The target runs straight on along the ground;
    /// airborne (a jump or a fall) it follows its parabola under the player's gravity down to
    /// <paramref name="floorY"/>, the height its aimed point rests at once it lands (see <see cref="AimTracker"/>).
    /// </summary>
    public static bool TryLeadVelocity(Vector3 from, Vector3 to, Vector3 targetVelocity, float floorY, float speed,
        float gravity, out Vector3 velocity)
    {
        var aim = to;
        bool reachable = TryLaunchVelocity(from, aim, speed, gravity, out velocity);
        for (int i = 0; i < LeadIterations; i++)
        {
            aim = PredictTarget(to, targetVelocity, floorY, FlightSeconds(from, aim, velocity));
            reachable = TryLaunchVelocity(from, aim, speed, gravity, out velocity);
        }

        return reachable;
    }

    /// <summary>Seconds a projectile launched at <paramref name="velocity"/> takes to cover the ground between
    /// <paramref name="from"/> and <paramref name="to"/>, capped at <see cref="MaxLeadFlightSeconds"/>; none for a
    /// straight-up or straight-down shot.</summary>
    private static float FlightSeconds(Vector3 from, Vector3 to, Vector3 velocity)
    {
        float horizontalSpeed = new Vector2(velocity.X, velocity.Z).Length();
        if (horizontalSpeed < 1e-3f) return 0f;
        float distance = new Vector2(to.X - from.X, to.Z - from.Z).Length();
        return MathF.Min(distance / horizontalSpeed, MaxLeadFlightSeconds);
    }

    /// <summary>Where a target at <paramref name="to"/> moving at <paramref name="velocity"/> is after
    /// <paramref name="seconds"/>: straight along the ground, on a gravity parabola that ends on
    /// <paramref name="floorY"/> when airborne.</summary>
    private static Vector3 PredictTarget(Vector3 to, Vector3 velocity, float floorY, float seconds)
    {
        float y = to.Y;
        if (MathF.Abs(velocity.Y) > AirborneSpeed)
            y = MathF.Max(to.Y + velocity.Y * seconds - 0.5f * PlayerPhysics.Gravity * seconds * seconds,
                MathF.Min(floorY, to.Y));
        return new Vector3(to.X + velocity.X * seconds, y, to.Z + velocity.Z * seconds);
    }

    /// <summary>Vertical speed above which the target counts as airborne; a grounded body reports none.</summary>
    private const float AirborneSpeed = 0.5f;

    /// <summary>Passes of the lead solve: each shrinks the error by the ratio of the target's speed to the
    /// projectile's, so a runner is met within a few hundredths of a block.</summary>
    private const int LeadIterations = 5;

    /// <summary>Caps the flight time the lead trusts, so a lob at a far target never leads it past reason.</summary>
    private const float MaxLeadFlightSeconds = 2f;
}
