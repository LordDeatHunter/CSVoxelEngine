using System;

namespace VoxelEngine.Audio;

/// <summary>
/// A randomized repeat timer for ambient calls: each interval is drawn uniformly from
/// [<c>minSeconds</c>, <c>maxSeconds</c>], so a call lands at irregular gaps rather than on a beat.
/// <see cref="Tick"/> reports the frame the timer expires and re-arms itself either way.
/// </summary>
internal sealed class AmbientCallTimer(Random random, float minSeconds, float maxSeconds)
{
    private float _timer = NextInterval(random, minSeconds, maxSeconds);

    /// <summary>Advances the timer; true on the frame it expires (and it re-arms for the next interval).</summary>
    public bool Tick(float deltaSeconds)
    {
        _timer -= deltaSeconds;
        if (_timer > 0f) return false;
        _timer = NextInterval(random, minSeconds, maxSeconds);
        return true;
    }

    private static float NextInterval(Random random, float minSeconds, float maxSeconds) =>
        minSeconds + random.NextSingle() * (maxSeconds - minSeconds);
}
