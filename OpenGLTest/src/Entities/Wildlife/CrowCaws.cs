using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Config;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// The crows' ambient caw: on a randomized timer, plays one clip of <see cref="SfxLibrary.CrowCawGroup"/>
/// when a live crow is within earshot of the camera, so a flock announces itself from time to time
/// without every crow calling at once. Not positional (the mixer has no panning), so only the range
/// gate says where it came from. Cosmetic and main-thread only: a headless server never builds one.
/// </summary>
internal sealed class CrowCaws(Action<string> play, Random random)
{
    private readonly AmbientCallTimer _timer = new(random, AudioConfig.CrowCawMinSeconds, AudioConfig.CrowCawMaxSeconds);

    /// <summary>One frame: ticks the timer against the crows currently near <paramref name="listener"/>.</summary>
    public void Tick(float deltaSeconds, IReadOnlyList<Animal> wildlife, Vector3 listener)
    {
        if (!_timer.Tick(deltaSeconds)) return;
        if (AnyWithin(wildlife, listener, AudioConfig.CrowCawRangeBlocks))
            play(SfxLibrary.CrowCawGroup);
    }

    /// <summary>True when any live crow is within <paramref name="rangeBlocks"/> of <paramref name="listener"/>.</summary>
    public static bool AnyWithin(IReadOnlyList<Animal> wildlife, Vector3 listener, float rangeBlocks)
    {
        var rangeSquared = rangeBlocks * rangeBlocks;
        foreach (var animal in wildlife)
        {
            if (animal is Crow crow && !crow.IsDead && Vector3.DistanceSquared(crow.Position, listener) <= rangeSquared)
                return true;
        }
        return false;
    }
}
