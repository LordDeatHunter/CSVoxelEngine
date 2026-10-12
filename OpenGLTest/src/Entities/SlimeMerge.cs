using System;
using System.Numerics;
using VoxelEngine.Simulation;

namespace VoxelEngine.Entities;

/// <summary>
/// The slime merge, GL-free: the inverse of the split (<see cref="SlimeSize.SplitsOnHit"/>). Two
/// living slimes of the same size below <see cref="SlimeSize.Max"/> whose bodies touch, each past
/// <see cref="WorldGenerationConfig.SlimeMergeCooldownSeconds"/> of life (so a split's children never
/// merge straight back), become one slime of the next size. <see cref="SlimeManager"/> applies it in
/// the authoritative simulation only, as a forced spawn under the global <see cref="MobCaps"/>.
/// </summary>
public static class SlimeMerge
{
    /// <summary>Whether <paramref name="slime"/> may merge at all: alive and not dying, below the
    /// largest size, and past the merge cooldown.</summary>
    public static bool IsReady(Slime slime) =>
        !slime.IsDying && !slime.IsDead && slime.Size < SlimeSize.Max
        && slime.AgeSeconds >= WorldGenerationConfig.SlimeMergeCooldownSeconds;

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> merge: both ready, the same size,
    /// and their collision boxes touch or overlap.</summary>
    public static bool CanMerge(Slime a, Slime b)
    {
        if (ReferenceEquals(a, b) || a.Size != b.Size || !IsReady(a) || !IsReady(b)) return false;
        var reach = a.BodyHalfExtent + b.BodyHalfExtent;
        var d = a.Position - b.Position;
        return MathF.Abs(d.X) <= reach && MathF.Abs(d.Y) <= a.BodyHalfHeight + b.BodyHalfHeight && MathF.Abs(d.Z) <= reach;
    }

    /// <summary>The size two slimes of <paramref name="size"/> merge into.</summary>
    public static int MergedSize(int size) => SlimeSize.Clamp(size + 1);

    /// <summary>
    /// The merged slime's health: the pair's share of their two pools carried over to
    /// <paramref name="mergedMax"/>, so two whole slimes make a whole one and a wounded pair a wounded
    /// one; at least 1 and at most the pool.
    /// </summary>
    public static int MergedHealth(int healthA, int maxA, int healthB, int maxB, int mergedMax)
    {
        var pools = Math.Max(1, maxA + maxB);
        var share = (float)(Math.Max(0, healthA) + Math.Max(0, healthB)) / pools;
        return Math.Clamp((int)MathF.Round(share * mergedMax), 1, Math.Max(1, mergedMax));
    }

    /// <summary>Where the merged slime's body center goes: midway between the two horizontally,
    /// resting a hair above the lower of their two feet (so float rounding never sinks it into the
    /// floor they stood on).</summary>
    public static Vector3 MergedCenter(Slime a, Slime b, int mergedSize)
    {
        var feet = MathF.Min(a.Position.Y - a.BodyHalfHeight, b.Position.Y - b.BodyHalfHeight) + 0.01f;
        return new Vector3((a.Position.X + b.Position.X) * 0.5f, feet + Slime.HalfExtentFor(mergedSize),
            (a.Position.Z + b.Position.Z) * 0.5f);
    }
}
