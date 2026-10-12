using System;
using System.Numerics;

namespace VoxelEngine.Entities;

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
}
