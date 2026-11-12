using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Audio;

namespace VoxelEngine.Entities.TowerMobs;

/// <summary>
/// The skeletons' ambient bone rattle: on a randomized timer, plays one clip of
/// <see cref="SfxLibrary.SkeletonRattleGroup"/> from the nearest skeleton within earshot of the camera,
/// so a tower sounds inhabited without every skeleton clacking constantly. Cosmetic and main-thread
/// only: each side rattles its own (a client its mirrors), and a headless server never builds one.
/// </summary>
internal sealed class SkeletonRattle(Action<string, Vector3> play, Random random)
{
    private readonly AmbientCallTimer _timer = new(random, AudioConfig.SkeletonRattleMinSeconds, AudioConfig.SkeletonRattleMaxSeconds);

    /// <summary>One frame: ticks the timer against the skeletons currently near <paramref name="listener"/>.</summary>
    public void Tick(float deltaSeconds, IReadOnlyList<Skeleton> skeletons, Vector3 listener) =>
        Update(deltaSeconds, NearestWithin(skeletons, listener, AudioConfig.SkeletonRattleRangeBlocks));

    /// <summary>
    /// Ticks the timer. When it expires the rattle plays at <paramref name="nearest"/>, the skeleton in earshot (null
    /// for none, which stays silent), and the timer re-arms either way, so walking up to a skeleton never triggers one
    /// at once.
    /// </summary>
    public void Update(float deltaSeconds, Vector3? nearest)
    {
        if (_timer.Tick(deltaSeconds) && nearest is { } at)
            play(SfxLibrary.SkeletonRattleGroup, at);
    }

    /// <summary>Where the nearest live skeleton within <paramref name="rangeBlocks"/> of <paramref name="listener"/>
    /// stands, or null when there is none.</summary>
    public static Vector3? NearestWithin(IReadOnlyList<Skeleton> skeletons, Vector3 listener, float rangeBlocks)
    {
        Vector3? nearest = null;
        var best = rangeBlocks * rangeBlocks;
        foreach (var skeleton in skeletons)
        {
            var distance = Vector3.DistanceSquared(skeleton.Position, listener);
            if (skeleton.Health <= 0 || distance > best) continue;
            best = distance;
            nearest = skeleton.Position;
        }
        return nearest;
    }
}
