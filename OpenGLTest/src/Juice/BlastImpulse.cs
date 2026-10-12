using System.Numerics;
using VoxelEngine.Simulation;

namespace VoxelEngine.Juice;

/// <summary>
/// The shove a TNT blast gives a loose thing in range, GL-free: a velocity change in blocks per
/// second pointing away from the blast centre (tipped up by <see cref="WorldGenerationConfig.BlastPushUpBias"/>,
/// so things are tossed rather than skated along the ground), strongest at the centre, where it is
/// <see cref="WorldGenerationConfig.BlastPushMaxSpeed"/>, and falling linearly to zero at the radius.
/// It never grows with distance and is zero outside the radius. <see cref="TntManager"/> applies it
/// to the drops, falling blocks and other primed TNT, and <see cref="World"/> to the players and mobs.
/// </summary>
public static class BlastImpulse
{
    /// <summary>The impulse a blast of <paramref name="radius"/> at <paramref name="centre"/> gives a
    /// thing at <paramref name="target"/>, with the configured strength and lift.</summary>
    public static Vector3 At(Vector3 centre, Vector3 target, float radius) =>
        At(centre, target, radius, WorldGenerationConfig.BlastPushMaxSpeed, WorldGenerationConfig.BlastPushUpBias);

    /// <summary>The impulse with an explicit <paramref name="maxSpeed"/> (at the centre) and
    /// <paramref name="upBias"/> (added to the unit direction before it is renormalised).</summary>
    public static Vector3 At(Vector3 centre, Vector3 target, float radius, float maxSpeed, float upBias)
    {
        if (!(radius > 0f) || !(maxSpeed > 0f)) return Vector3.Zero;
        var away = target - centre;
        var distance = away.Length();
        if (!(distance < radius)) return Vector3.Zero;

        // A thing sitting exactly on the centre goes straight up
        var direction = distance > 1e-4f ? away / distance : Vector3.UnitY;
        var tipped = direction + new Vector3(0f, upBias, 0f);
        if (tipped.LengthSquared() > 1e-6f)
            direction = Vector3.Normalize(tipped);
        return direction * (maxSpeed * (1f - distance / radius));
    }
}
