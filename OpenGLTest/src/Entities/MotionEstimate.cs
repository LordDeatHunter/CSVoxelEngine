using System;
using System.Numerics;

namespace VoxelEngine.Entities;

/// <summary>
/// A body's velocity, estimated from where it is seen frame after frame: for a target that only reports
/// positions (a server's connected player, whose eye arrives in network states). Each observation blends
/// the frame's displacement into a running average over <see cref="MobConfig.MobMotionSmoothingSeconds"/>,
/// so the gaps between network states even out; a jump faster than
/// <see cref="MobConfig.MobMotionMaxSpeed"/> is a teleport and restarts the estimate at rest. GL-free.
/// </summary>
public sealed class MotionEstimate
{
    private Vector3 _last;
    private bool _seen;

    /// <summary>The estimated velocity in blocks per second; zero until two observations were made.</summary>
    public Vector3 Velocity { get; private set; }

    /// <summary>Takes the body's <paramref name="position"/> after <paramref name="deltaTime"/> more seconds.</summary>
    public void Observe(Vector3 position, float deltaTime)
    {
        if (deltaTime <= 0f) return;
        if (!_seen)
        {
            _last = position;
            _seen = true;
            return;
        }

        var instant = (position - _last) / deltaTime;
        _last = position;
        if (instant.LengthSquared() > MobConfig.MobMotionMaxSpeed * MobConfig.MobMotionMaxSpeed)
        {
            Velocity = Vector3.Zero;
            return;
        }

        var blend = MathF.Min(1f, deltaTime / MobConfig.MobMotionSmoothingSeconds);
        Velocity = Vector3.Lerp(Velocity, instant, blend);
    }
}
