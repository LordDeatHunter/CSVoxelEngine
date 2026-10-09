using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// GL-free rules the mob managers share for choosing and touching their <see cref="IMobTarget"/>s:
/// which target a mob chases, how far its nearest target is (despawn), and whether a mob's body
/// touches a player's body box.
/// </summary>
public static class MobTargets
{
    /// <summary>
    /// Whether an entity cube at <paramref name="center"/> with the given half extent touches the
    /// body box of a player whose eye is at <paramref name="eye"/>. Bodies only touch when they
    /// genuinely overlap: a face flush against the body does not count.
    /// </summary>
    public static bool BodyTouches(Vector3 eye, Vector3 center, float halfExtent)
    {
        var min = new Vector3(
            eye.X - PlayerPhysics.PlayerRadius,
            eye.Y - Camera.EyeHeight,
            eye.Z - PlayerPhysics.PlayerRadius);
        var max = new Vector3(
            eye.X + PlayerPhysics.PlayerRadius,
            eye.Y - Camera.EyeHeight + PlayerPhysics.PlayerHeight,
            eye.Z + PlayerPhysics.PlayerRadius);
        var eMin = center - new Vector3(halfExtent);
        var eMax = center + new Vector3(halfExtent);

        return min.X < eMax.X && max.X > eMin.X
            && min.Y < eMax.Y && max.Y > eMin.Y
            && min.Z < eMax.Z && max.Z > eMin.Z;
    }

    /// <summary>
    /// Index of the target a mob at <paramref name="position"/> chases: the horizontally nearest
    /// one that is not immune, else (every target immune) the nearest immune one, so mobs still
    /// face a lone hovering camera. -1 when there are no targets.
    /// </summary>
    public static int Nearest(IReadOnlyList<IMobTarget> targets, Vector3 position)
    {
        var best = -1;
        var bestImmune = -1;
        var bestSquared = float.PositiveInfinity;
        var bestImmuneSquared = float.PositiveInfinity;

        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            var squared = HorizontalDistanceSquared(target.Eye, position);
            if (target.IsImmune)
            {
                if (squared < bestImmuneSquared)
                {
                    bestImmuneSquared = squared;
                    bestImmune = i;
                }
            }
            else if (squared < bestSquared)
            {
                bestSquared = squared;
                best = i;
            }
        }

        return best >= 0 ? best : bestImmune;
    }

    /// <summary>The squared horizontal distance from <paramref name="position"/> to the nearest
    /// target, immune or not; <see cref="float.PositiveInfinity"/> when there are none.</summary>
    public static float MinHorizontalDistanceSquared(IReadOnlyList<IMobTarget> targets, Vector3 position)
    {
        var min = float.PositiveInfinity;
        for (var i = 0; i < targets.Count; i++)
        {
            var squared = HorizontalDistanceSquared(targets[i].Eye, position);
            if (squared < min) min = squared;
        }

        return min;
    }

    private static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }
}
