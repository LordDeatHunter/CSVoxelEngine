using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Audio;

namespace VoxelEngine.Entities.TowerMobs;

/// <summary>
/// The skeletons' ambient bone rattle: on a randomized timer, plays one clip of
/// <see cref="SfxLibrary.SkeletonRattleGroup"/> when a skeleton is within earshot of the camera,
/// so a tower sounds inhabited without every skeleton clacking constantly. The sound is not
/// positional (the mixer has no panning), so only the range gate says where it came from.
/// Cosmetic and main-thread only: a headless server never builds one.
/// </summary>
internal sealed class SkeletonRattle(Action<string> play, Random random)
{
    private readonly AmbientCallTimer _timer = new(random, AudioConfig.SkeletonRattleMinSeconds, AudioConfig.SkeletonRattleMaxSeconds);

    /// <summary>One frame: ticks the timer against the skeletons currently near <paramref name="listener"/>.</summary>
    public void Tick(float deltaSeconds, IReadOnlyList<Skeleton> skeletons, Vector3 listener) =>
        Update(deltaSeconds, AnyWithin(skeletons, listener, AudioConfig.SkeletonRattleRangeBlocks));

    /// <summary>
    /// Ticks the timer. When it expires the rattle plays only if <paramref name="skeletonNearby"/>,
    /// and the timer re-arms either way, so walking up to a skeleton never triggers one at once.
    /// </summary>
    public void Update(float deltaSeconds, bool skeletonNearby)
    {
        if (_timer.Tick(deltaSeconds) && skeletonNearby)
            play(SfxLibrary.SkeletonRattleGroup);
    }

    /// <summary>True when any live skeleton is within <paramref name="rangeBlocks"/> of <paramref name="listener"/>.</summary>
    public static bool AnyWithin(IReadOnlyList<Skeleton> skeletons, Vector3 listener, float rangeBlocks)
    {
        var rangeSquared = rangeBlocks * rangeBlocks;
        foreach (var skeleton in skeletons)
        {
            if (skeleton.Health > 0 && Vector3.DistanceSquared(skeleton.Position, listener) <= rangeSquared)
                return true;
        }
        return false;
    }
}
