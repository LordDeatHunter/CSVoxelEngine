using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Audio;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// One species' ambient call (the crows' caw, the boars' grunt): on a randomized timer, plays one clip of
/// <paramref name="group"/> from the nearest live animal of <paramref name="kind"/> within earshot of the camera,
/// so a flock or herd announces itself from time to time without every animal calling at once. Cosmetic and
/// main-thread only: each side calls for its own (a client its mirrors), and a headless server never builds one.
/// </summary>
internal sealed class WildlifeCalls(Action<string, Vector3> play, Random random, WildlifeKind kind, string group,
    float rangeBlocks, float minSeconds, float maxSeconds)
{
    private readonly AmbientCallTimer _timer = new(random, minSeconds, maxSeconds);

    /// <summary>One frame: ticks the timer against the animals currently near <paramref name="listener"/>.</summary>
    public void Tick(float deltaSeconds, IReadOnlyList<Animal> wildlife, Vector3 listener)
    {
        if (!_timer.Tick(deltaSeconds)) return;
        if (NearestWithin(wildlife, kind, listener, rangeBlocks) is { } at)
            play(group, at);
    }

    /// <summary>Where the nearest live animal of <paramref name="kind"/> within <paramref name="rangeBlocks"/> of
    /// <paramref name="listener"/> is, or null when there is none.</summary>
    public static Vector3? NearestWithin(IReadOnlyList<Animal> wildlife, WildlifeKind kind, Vector3 listener,
        float rangeBlocks)
    {
        Vector3? nearest = null;
        var best = rangeBlocks * rangeBlocks;
        foreach (var animal in wildlife)
        {
            if (animal.Kind != kind || animal.IsDead) continue;
            var distance = Vector3.DistanceSquared(animal.Position, listener);
            if (distance > best) continue;
            best = distance;
            nearest = animal.Position;
        }
        return nearest;
    }
}
