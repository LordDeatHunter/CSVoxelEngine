using System;

namespace VoxelEngine.Particles;

/// <summary>
/// One body's memory for its water splashes, GL-free: whether it was in water last frame and how fast
/// it was moving up or down, measured from its own height between calls, so a client's mirror whose
/// position only jumps once per snapshot reads the speed it covered that height in, not the jump.
/// <see cref="Step"/> reports a splash when the body enters or leaves water, with the entry's
/// downward or the exit's upward speed; <see cref="WaterSplash"/> turns that into droplets. The first
/// call only seeds (a body that appears in water does not splash), a step farther than
/// <see cref="WorldGenerationConfig.WaterSplashTeleportBlocks"/> is a teleport and splashes nothing,
/// and a body that sat still for <see cref="WorldGenerationConfig.WaterSplashStaleSeconds"/> counts as
/// still, so water rising over a resting drop raises no splash. Main thread only.
/// </summary>
public sealed class WaterSplashTracker
{
    private bool _seeded;
    private bool _wasInWater;
    private float _lastY;
    private float _sinceMove;
    private float _speed;

    /// <summary>
    /// One frame of the body: <paramref name="inWater"/> now, its reference height
    /// <paramref name="y"/> (its lowest point, or wherever the caller tests the water) and the time
    /// since the last call. True when it just crossed into or out of water while moving that way,
    /// with <paramref name="speed"/> (blocks per second, never negative) its downward speed for an
    /// entry or its upward one for an exit.
    /// </summary>
    public bool Step(bool inWater, float y, float deltaTime, out float speed)
    {
        speed = 0f;
        if (!_seeded)
        {
            _seeded = true;
            _wasInWater = inWater;
            _lastY = y;
            return false;
        }

        _sinceMove += Math.Max(0f, deltaTime);
        var teleported = false;
        var dy = y - _lastY;
        if (MathF.Abs(dy) > 1e-5f)
        {
            teleported = MathF.Abs(dy) > WorldGenerationConfig.WaterSplashTeleportBlocks;
            _speed = teleported ? 0f : dy / MathF.Max(_sinceMove, 1e-3f);
            _lastY = y;
            _sinceMove = 0f;
        }

        var crossed = inWater != _wasInWater;
        _wasInWater = inWater;
        if (!crossed || teleported) return false;

        var current = _sinceMove > WorldGenerationConfig.WaterSplashStaleSeconds ? 0f : _speed;
        speed = inWater ? MathF.Max(0f, -current) : MathF.Max(0f, current);
        return speed > 0f;
    }
}
