using System;
using System.Numerics;

namespace VoxelEngine.Core;

/// <summary>
/// The GL-free, state-free velocity steps every loose body shares: item drops, primed TNT, falling
/// blocks, thrown projectiles, goblins, slimes and mining debris. Each caller keeps its own test of
/// which point of its body is under the fluid surface and its own guards (grounded, landed,
/// sliding); only the steps and their numbers live here. The player is not a loose body: it keeps
/// its own gravity (<c>PlayerPhysics.Gravity</c>) and water rules. Main thread.
/// </summary>
internal static class BodyMotion
{
    /// <summary>Downward acceleration of every loose body, in blocks/s^2.</summary>
    internal const float Gravity = 28f;
    /// <summary>The fastest a loose body falls, in blocks/s (negative: down).</summary>
    internal const float TerminalVelocity = -30f;
    /// <summary>How fast friction scrubs the horizontal speed of a body resting on the ground, per second.</summary>
    internal const float GroundFriction = 8f;

    /// <summary>The speed a heavy body (a falling cube, primed TNT, a debris chip) settles to while it sinks, in blocks/s.</summary>
    internal const float SinkSpeed = -3f;
    /// <summary>Damp rate of a sinking body's vertical speed toward <see cref="SinkSpeed"/>.</summary>
    internal const float SinkDrag = 5f;
    /// <summary>Share of the impact a heavy body (primed TNT, a debris chip) bounces back up with.</summary>
    internal const float HeavyBounceDamping = 0.35f;
    /// <summary>The smallest impact, in blocks/s, a heavy body bounces from; a gentler one rests.</summary>
    internal const float HeavyBounceThreshold = 4f;

    /// <summary>The fastest a floating body rises or sinks toward the surface, in blocks/s.</summary>
    internal const float FloatSpeed = 1.5f;
    /// <summary>Rise speed per block a floating body's bottom is under the surface.</summary>
    internal const float BuoyancyStiffness = 3f;
    /// <summary>Damp rate of a floating body's vertical speed toward its rise.</summary>
    internal const float FloatDrag = 4f;
    /// <summary>How fast water scrubs a floating body's horizontal speed, per second.</summary>
    internal const float WaterFriction = 3f;

    /// <summary>The vertical speed after <paramref name="deltaTime"/> of falling under <see cref="Gravity"/>, capped at <see cref="TerminalVelocity"/>.</summary>
    internal static float Fall(float velocityY, float deltaTime) =>
        MathF.Max(velocityY - Gravity * deltaTime, TerminalVelocity);

    /// <summary>The vertical speed of a heavy body: <paramref name="submerged"/>, it eases toward <see cref="SinkSpeed"/> instead of floating; otherwise it falls (<see cref="Fall"/>).</summary>
    internal static float SinkOrFall(float velocityY, bool submerged, float deltaTime)
    {
        if (submerged)
            return MathUtils.Damp(velocityY, SinkSpeed, SinkDrag, deltaTime);
        return Fall(velocityY, deltaTime);
    }

    /// <summary>
    /// Buoyancy: instead of gravity, a floating body eases toward a rise proportional to how far its
    /// <paramref name="bottomY"/> is under <paramref name="surfaceY"/>, so it climbs, barely
    /// overshoots and bobs at the top. Vertical only; <see cref="DragInWater"/> is the horizontal half.
    /// </summary>
    internal static float FloatTowardSurface(float velocityY, float surfaceY, float bottomY, float deltaTime)
    {
        var targetY = Math.Clamp((surfaceY - bottomY) * BuoyancyStiffness, -FloatSpeed, FloatSpeed);
        return MathUtils.Damp(velocityY, targetY, FloatDrag, deltaTime);
    }

    /// <summary>Scrubs a floating body's horizontal speed by <see cref="WaterFriction"/>.</summary>
    internal static void DragInWater(ref Vector3 velocity, float deltaTime)
    {
        var drag = MathF.Exp(-WaterFriction * deltaTime);
        velocity.X *= drag;
        velocity.Z *= drag;
    }

    /// <summary>The upward speed a landing at <paramref name="impact"/> bounces back with: <paramref name="damping"/> of it from <paramref name="minImpact"/> up, none below it or when the water <paramref name="absorbed"/> the impact.</summary>
    internal static float Bounce(float impact, bool absorbed, float minImpact, float damping) =>
        !absorbed && impact >= minImpact ? impact * damping : 0f;

    /// <summary>Damps a resting body's horizontal speed by <see cref="GroundFriction"/>.</summary>
    internal static void DampOnGround(ref Vector3 velocity, float deltaTime)
    {
        var damp = MathF.Max(0f, 1f - GroundFriction * deltaTime);
        velocity.X *= damp;
        velocity.Z *= damp;
    }

    /// <summary><see cref="DampOnGround"/>, then stops a horizontal crawl under 0.01 blocks/s outright.</summary>
    internal static void ScrubOnGround(ref Vector3 velocity, float deltaTime)
    {
        DampOnGround(ref velocity, deltaTime);
        if (MathF.Abs(velocity.X) < 0.01f) velocity.X = 0f;
        if (MathF.Abs(velocity.Z) < 0.01f) velocity.Z = 0f;
    }
}
