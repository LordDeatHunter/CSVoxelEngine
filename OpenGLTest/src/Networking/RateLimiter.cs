using System;

namespace VoxelEngine.Networking;

/// <summary>
/// A token bucket: <see cref="TryTake"/> succeeds while tokens are left, and the bucket refills at a
/// fixed rate up to its burst size, so a steady stream at the rate (and a short burst above it)
/// passes and a sustained flood is refused. Time is whatever clock the caller passes,
/// monotonically. GL-free; one owner thread.
/// </summary>
public sealed class RateLimiter
{
    private readonly float _perSecond;
    private readonly float _burst;
    private float _tokens;
    private double _last = double.NaN;

    /// <summary>A bucket refilling <paramref name="perSecond"/> tokens a second, holding at most
    /// <paramref name="burst"/>, and starting full.</summary>
    public RateLimiter(float perSecond, float burst)
    {
        _perSecond = perSecond;
        _burst = burst;
        _tokens = burst;
    }

    /// <summary>Takes one token at time <paramref name="now"/>; false (taking nothing) when the
    /// bucket is empty.</summary>
    public bool TryTake(double now)
    {
        if (!double.IsNaN(_last) && now > _last)
            _tokens = (float)Math.Min(_burst, _tokens + (now - _last) * _perSecond);
        if (double.IsNaN(_last) || now > _last)
            _last = now;
        if (_tokens < 1f)
            return false;
        _tokens -= 1f;
        return true;
    }
}
