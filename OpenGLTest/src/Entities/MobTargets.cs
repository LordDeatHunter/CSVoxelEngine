using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Player;

namespace VoxelEngine.Entities;

/// <summary>
/// GL-free rules the mob managers share for choosing and touching their <see cref="IMobTarget"/>s:
/// which target a mob chases, how far its nearest target is (despawn), and whether a mob's body
/// touches a player's body box.
/// </summary>
public static class MobTargets
{
    /// <summary>
    /// Whether a mob body centered at <paramref name="mobCenter"/> can land a melee swing of
    /// <paramref name="reach"/> blocks on a player whose eye is at <paramref name="eye"/>: horizontally
    /// within the reach and roughly level with the player's body center.
    /// </summary>
    public static bool InMeleeReach(Vector3 mobCenter, Vector3 eye, float reach)
    {
        var dx = eye.X - mobCenter.X;
        var dz = eye.Z - mobCenter.Z;
        if (dx * dx + dz * dz > reach * reach) return false;
        return MathF.Abs(mobCenter.Y - (eye.Y - Camera.EyeHeight * 0.5f)) <= 1.5f;
    }

    /// <summary>
    /// Whether a ground shockwave from a mob standing at <paramref name="feet"/> reaches a player whose eye is at
    /// <paramref name="eye"/>: within <paramref name="radius"/> blocks horizontally, the player's feet within
    /// <paramref name="height"/> blocks of the mob's (a cloud golem's slam, the Skeleton King's).
    /// </summary>
    public static bool InShockwave(Vector3 feet, Vector3 eye, float radius, float height)
    {
        var dx = eye.X - feet.X;
        var dz = eye.Z - feet.Z;
        if (dx * dx + dz * dz > radius * radius) return false;
        return MathF.Abs(eye.Y - Camera.EyeHeight - feet.Y) <= height;
    }

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
    /// How far, horizontally, a mob notices <paramref name="target"/> from:
    /// <see cref="MobConfig.MobNoticeRadiusBlocks"/>, times
    /// <see cref="MobConfig.MobSneakNoticeFactor"/> while the target sneaks.
    /// </summary>
    public static float NoticeRadius(IMobTarget target) =>
        MobConfig.MobNoticeRadiusBlocks * (target.IsSneaking ? MobConfig.MobSneakNoticeFactor : 1f);

    /// <summary>
    /// Index of the target a mob at <paramref name="position"/> chases: the horizontally nearest
    /// one that is not immune, else (every target immune) the nearest immune one, so mobs still
    /// face a lone hovering camera. A target farther than its <see cref="NoticeRadius"/> (shorter
    /// while it sneaks) goes unnoticed. -1 when no target is noticed.
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
            var notice = NoticeRadius(target);
            if (squared > notice * notice) continue;
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

    /// <summary>
    /// Whether no target keeps a mob at <paramref name="position"/> in play, so it fades out: every one is
    /// farther than <paramref name="despawnRadius"/> horizontally, or has left it behind (it moves at
    /// <see cref="MobConfig.MobSpawnAheadMinSpeed"/> or faster, and the mob lies more than
    /// <see cref="MobConfig.MobLeftBehindBlocks"/> away in the third of the circle behind its heading). True with
    /// no targets at all; the managers only ask while somebody is in play.
    /// </summary>
    public static bool OutOfPlay(IReadOnlyList<IMobTarget> targets, Vector3 position, float despawnRadius)
    {
        for (var i = 0; i < targets.Count; i++)
        {
            var eye = targets[i].Eye;
            var dx = position.X - eye.X;
            var dz = position.Z - eye.Z;
            var squared = dx * dx + dz * dz;
            if (squared > despawnRadius * despawnRadius) continue;
            if (!LeftBehind(targets[i].Velocity, dx, dz, squared)) return false;
        }

        return true;
    }

    // Whether a target moving at velocity has left behind a mob (dx, dz) away from it: far enough, and
    // more than 120 degrees off its heading (the cosine of the angle between them under -0.5)
    private static bool LeftBehind(Vector3 velocity, float dx, float dz, float squared)
    {
        if (squared <= MobConfig.MobLeftBehindBlocks * MobConfig.MobLeftBehindBlocks) return false;
        var speedSquared = velocity.X * velocity.X + velocity.Z * velocity.Z;
        if (speedSquared < MobConfig.MobSpawnAheadMinSpeed * MobConfig.MobSpawnAheadMinSpeed) return false;
        return dx * velocity.X + dz * velocity.Z < -0.5f * MathF.Sqrt(squared * speedSquared);
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
