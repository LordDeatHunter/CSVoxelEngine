using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Config;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// The crows' ambient caw: on a randomized timer, plays one clip of <see cref="SfxLibrary.CrowCawGroup"/>
/// from the nearest live crow within earshot of the camera, so a flock announces itself from time to time
/// without every crow calling at once. Cosmetic and main-thread only: each side caws its own (a client its
/// mirrors), and a headless server never builds one.
/// </summary>
internal sealed class CrowCaws(Action<string, Vector3> play, Random random)
{
    private readonly AmbientCallTimer _timer = new(random, AudioConfig.CrowCawMinSeconds, AudioConfig.CrowCawMaxSeconds);

    /// <summary>One frame: ticks the timer against the crows currently near <paramref name="listener"/>.</summary>
    public void Tick(float deltaSeconds, IReadOnlyList<Animal> wildlife, Vector3 listener)
    {
        if (!_timer.Tick(deltaSeconds)) return;
        if (NearestWithin(wildlife, listener, AudioConfig.CrowCawRangeBlocks) is { } at)
            play(SfxLibrary.CrowCawGroup, at);
    }

    /// <summary>Where the nearest live crow within <paramref name="rangeBlocks"/> of <paramref name="listener"/> is,
    /// or null when there is none.</summary>
    public static Vector3? NearestWithin(IReadOnlyList<Animal> wildlife, Vector3 listener, float rangeBlocks)
    {
        Vector3? nearest = null;
        var best = rangeBlocks * rangeBlocks;
        foreach (var animal in wildlife)
        {
            if (animal is not Crow crow || crow.IsDead) continue;
            var distance = Vector3.DistanceSquared(crow.Position, listener);
            if (distance > best) continue;
            best = distance;
            nearest = crow.Position;
        }
        return nearest;
    }
}
