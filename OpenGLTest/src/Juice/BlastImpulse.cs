using System.Numerics;
using VoxelEngine.Simulation;

namespace VoxelEngine.Juice;

/// <summary>
/// The shove a TNT blast gives a loose thing in range, GL-free: a velocity change in blocks per
/// second pointing away from the blast center (tipped up by <see cref="WorldGenerationConfig.BlastPushUpBias"/>,
/// so things are tossed rather than skated along the ground), strongest at the center, where it is
/// <see cref="WorldGenerationConfig.BlastPushMaxSpeed"/>, and falling linearly to zero at the radius.
/// It never grows with distance and is zero outside the radius. <see cref="TntManager"/> applies it
/// to the drops, falling blocks and other primed TNT, and <see cref="World"/> to the players and mobs.
/// </summary>
public static class BlastImpulse
{
    /// <summary>The impulse a blast of <paramref name="radius"/> at <paramref name="center"/> gives a
    /// thing at <paramref name="target"/>, with the configured strength and lift.</summary>
    public static Vector3 At(Vector3 center, Vector3 target, float radius) =>
        At(center, target, radius, WorldGenerationConfig.BlastPushMaxSpeed, WorldGenerationConfig.BlastPushUpBias);

    /// <summary>The impulse with an explicit <paramref name="maxSpeed"/> (at the center) and
    /// <paramref name="upBias"/> (added to the unit direction before it is renormalized).</summary>
    public static Vector3 At(Vector3 center, Vector3 target, float radius, float maxSpeed, float upBias)
    {
        if (!(radius > 0f) || !(maxSpeed > 0f)) return Vector3.Zero;
        var away = target - center;
        var distance = away.Length();
        if (!(distance < radius)) return Vector3.Zero;

        // A thing sitting exactly on the center goes straight up
        var direction = distance > 1e-4f ? away / distance : Vector3.UnitY;
        var tipped = direction + new Vector3(0f, upBias, 0f);
        if (tipped.LengthSquared() > 1e-6f)
            direction = Vector3.Normalize(tipped);
        return direction * (maxSpeed * (1f - distance / radius));
    }
}
