using System;
using System.Numerics;

namespace VoxelEngine.Effects.Sky;

/// <summary>
/// GL-free twin of the night dome's shooting stars, drawn by <c>ShootingStar</c> in
/// sky-fragment.glsl; keep the two in step. Time is cut into slots of
/// <see cref="SkyConfig.ShootingStarSlotSeconds"/> (the star clock wraps hourly and the slot length
/// divides an hour, so the slots wrap with it); a slot holds a streak with probability
/// <see cref="SkyConfig.ShootingStarChance"/>, hashed from its index, starting at a hashed moment
/// inside the slot and living <see cref="SkyConfig.ShootingStarLifeSeconds"/>, so one never outlasts
/// its slot and a single slot is all a pixel has to look at. A streak is the head sliding along a
/// great circle through a hashed point of the upper sky in a hashed heading, with a tail behind
/// it that fades to nothing, all fixed in world directions like the stars.
/// </summary>
public static class ShootingStar
{
    /// <summary>One streak: the great circle's start point and unit tangent, and when in its slot it starts.</summary>
    public readonly record struct Streak(Vector3 Start, Vector3 Tangent, float StartSeconds)
    {
        /// <summary>The unit normal of the streak's great-circle plane.</summary>
        public Vector3 Normal => Vector3.Cross(Start, Tangent);
    }

    /// <summary>The slot a time falls in and the seconds into it.</summary>
    public static (int Slot, float Local) SlotOf(float time)
    {
        var slot = (int)MathF.Floor(time / SkyConfig.ShootingStarSlotSeconds);
        return (slot, time - slot * SkyConfig.ShootingStarSlotSeconds);
    }

    /// <summary>The streak of slot <paramref name="slot"/>, if the slot holds one (most do not).</summary>
    public static bool TryGetStreak(int slot, out Streak streak)
    {
        var h = StarField.Hash(unchecked((uint)slot ^ 0x5bd1e995u));
        streak = default;
        if (StarField.Unit(h) >= SkyConfig.ShootingStarChance) return false;

        var h2 = StarField.Hash(h);
        var h3 = StarField.Hash(h2);
        var h4 = StarField.Hash(h3);
        var h5 = StarField.Hash(h4);
        var sinEl = MathUtils.Lerp(SkyConfig.ShootingStarMinSinElevation, SkyConfig.ShootingStarMaxSinElevation,
            StarField.Unit(h2));
        var cosEl = MathF.Sqrt(1f - sinEl * sinEl);
        var az = StarField.Unit(h3) * MathF.Tau;
        var start = new Vector3(cosEl * MathF.Cos(az), sinEl, cosEl * MathF.Sin(az));
        // Any heading in the sky's tangent plane at the start: the horizontal one turned toward the zenith
        var east = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, start));
        var north = Vector3.Cross(start, east);
        var heading = StarField.Unit(h4) * MathF.Tau;
        var tangent = east * MathF.Cos(heading) + north * MathF.Sin(heading);
        var startSeconds = StarField.Unit(h5)
            * MathF.Max(SkyConfig.ShootingStarSlotSeconds - SkyConfig.ShootingStarLifeSeconds, 0f);
        streak = new Streak(start, tangent, startSeconds);
        return true;
    }

    /// <summary>
    /// The head's and the tail's angle along the streak's great circle, <paramref name="seconds"/>
    /// into its life (0..<see cref="SkyConfig.ShootingStarLifeSeconds"/>); the tail trails the head
    /// by <see cref="SkyConfig.ShootingStarTrailDegrees"/> and is cut at the start point.
    /// </summary>
    public static (float Head, float Tail) Span(float seconds)
    {
        var head = seconds / SkyConfig.ShootingStarLifeSeconds * MathUtils.ToRadians(SkyConfig.ShootingStarArcDegrees);
        return (head, MathF.Max(head - MathUtils.ToRadians(SkyConfig.ShootingStarTrailDegrees), 0f));
    }

    /// <summary>
    /// The streak's light in world direction <paramref name="d"/> at <paramref name="time"/> (the
    /// wrapped star clock) before <see cref="SkyConfig.ShootingStarBrightness"/>, 0..1:
    /// <paramref name="px"/> being one pixel's angular size, a gaussian
    /// <see cref="SkyConfig.ShootingStarRadiusPixels"/> wide across the path, brightest at the head
    /// and fading quadratically down the tail, and eased in and out over its life.
    /// </summary>
    public static float Light(Vector3 d, float time, float px)
    {
        var (slot, local) = SlotOf(time);
        if (!TryGetStreak(slot, out var streak)) return 0f;
        var seconds = local - streak.StartSeconds;
        if (seconds < 0f || seconds > SkyConfig.ShootingStarLifeSeconds) return 0f;

        var (head, tail) = Span(seconds);
        var phi = MathF.Atan2(Vector3.Dot(d, streak.Tangent), Vector3.Dot(d, streak.Start));
        if (phi < tail || phi > head) return 0f;

        var across = Vector3.Dot(d, streak.Normal) / (px * SkyConfig.ShootingStarRadiusPixels);
        var along = (phi - tail) / MathF.Max(head - tail, 1e-4f);
        var life = MathF.Sin(MathF.PI * seconds / SkyConfig.ShootingStarLifeSeconds);
        return along * along * life * MathF.Exp(-across * across);
    }
}
