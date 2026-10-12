using System;

namespace silkgltriangle;

/// <summary>
/// GL-free hit-stop of one swing clock (<see cref="PlayerModel"/>'s punch or slice). Once a swing
/// that connected reaches its impact pose, the clock holds there for a beat so the hit reads
/// heavier; afterwards the rest of the swing replays faster, so the swing still ends exactly when it
/// would have without the hold. A swing therefore never runs long: nothing that waits on it (the
/// slice combo's next swing, the next jab) is delayed, and attack pacing never sees it. It only maps
/// elapsed time to the swing's progress; the world, the mobs and the player's movement never stop.
/// </summary>
public sealed class SwingHitStop
{
    // Seconds of hold asked for, waiting for the swing to reach its impact pose (0 = none)
    private float _requested;
    // Seconds of hold left once it has begun
    private float _hold;
    // Progress-rate multiplier after the hold, which wins back the time the hold took
    private float _catchUp = 1f;
    // This swing already froze once: one hold per swing, so back-to-back requests never add up
    private bool _spent;

    /// <summary>True while the swing is held at its impact pose.</summary>
    public bool IsHolding => _hold > 0f;

    /// <summary>
    /// How long a connecting swing of <paramref name="weight"/> (0 a light swing or a slice, 1 the
    /// slowest tool's punch, see <see cref="PlayerModel.Punch"/>) asks to hold: never shorter for a
    /// heavier swing, never past <see cref="WorldGenerationConfig.HitStopMaxSeconds"/>.
    /// </summary>
    public static float SecondsFor(float weight) =>
        MathF.Min(MathUtils.Lerp(WorldGenerationConfig.HitStopSeconds, WorldGenerationConfig.HitStopHeavySeconds, weight),
            WorldGenerationConfig.HitStopMaxSeconds);

    /// <summary>A new swing started: forget any hold the last one asked for or still has.</summary>
    public void Reset()
    {
        _requested = 0f;
        _hold = 0f;
        _catchUp = 1f;
        _spent = false;
    }

    /// <summary>
    /// The swing connected: hold it for <paramref name="seconds"/> (clamped to
    /// <see cref="WorldGenerationConfig.HitStopMaxSeconds"/>) once it reaches its impact pose, or at
    /// once if it is already past it. One hold per swing: another request before the hold begins only
    /// lengthens it up to the cap, and one after it began is ignored, so holds never add up.
    /// </summary>
    public void Request(float seconds)
    {
        if (_spent || !(seconds > 0f))
            return;
        _requested = MathF.Max(_requested, MathF.Min(seconds, WorldGenerationConfig.HitStopMaxSeconds));
    }

    /// <summary>
    /// Advances a swing's <paramref name="progress"/> (0..1) by <paramref name="deltaTime"/> seconds at
    /// <paramref name="rate"/> progress per second, holding at <paramref name="impactAt"/> while a
    /// requested hold runs. Returns the new progress, clamped to 1. With no request it is exactly the
    /// plain clock.
    /// </summary>
    public float Advance(float progress, float deltaTime, float rate, float impactAt)
    {
        if (deltaTime <= 0f || rate <= 0f)
            return progress;

        if (_requested > 0f)
        {
            // The hold begins at the impact pose, part-way through this frame if the frame crosses it
            var toImpact = (impactAt - progress) / rate;
            if (toImpact <= deltaTime)
            {
                if (toImpact > 0f)
                {
                    deltaTime -= toImpact;
                    progress = impactAt;
                }
                Begin(progress, rate);
            }
        }

        if (_hold > 0f)
        {
            var held = MathF.Min(deltaTime, _hold);
            _hold -= held;
            deltaTime -= held;
        }

        return MathF.Min(1f, progress + deltaTime * rate * _catchUp);
    }

    private void Begin(float progress, float rate)
    {
        var remaining = MathF.Max(0f, 1f - progress) / rate;
        var hold = MathF.Min(_requested, remaining * WorldGenerationConfig.HitStopMaxRecoveryShare);
        _requested = 0f;
        _spent = true;
        if (hold <= 0f)
            return;
        _hold = hold;
        // The remaining swing must fit in what is left after the hold; the share keeps that positive.
        _catchUp = remaining / (remaining - hold);
    }
}
