using System;

namespace silkgltriangle;

/// <summary>
/// The fall-damage rule, GL-free: tracks the highest feet position since the body last
/// stood on ground and bills the drop through <see cref="PlayerHealth.FallDamage"/> when it
/// lands. Fed one observation per tick by its owner (<see cref="World"/> once a frame for
/// the local player), from explicit feet heights and body flags, so it never reads a camera.
/// A step of more than <c>maxStepBlocks</c> between two observations is a teleport (spawn
/// settle, scripts, a respawn), never a fall; flight forgets the fall; landing in water or
/// in creative is free.
/// </summary>
public sealed class FallTracker
{
    /// <summary>
    /// Singleplayer's teleport guard: the largest feet step between two 60 Hz frames that
    /// still counts as motion rather than a teleport.
    /// </summary>
    public const float DefaultMaxStepBlocks = 4f;

    private float _fallPeakFeet;
    private float _prevFeet;
    private bool _wasGrounded = true;
    private bool _hasFallState;

    /// <summary>
    /// The drop in blocks the latest <see cref="Observe"/> landed (grounded after airborne), whether
    /// or not it billed damage (water, creative), or 0 when that observation landed nothing. The
    /// landing dust reads it; a teleport, flight or a reset never lands.
    /// </summary>
    public float LandedDrop { get; private set; }

    /// <summary>
    /// Feeds one observation and returns the fall damage it lands, 0 when nothing lands.
    /// The first observation only seeds the state. A landing (grounded after airborne) bills
    /// the drop from the peak feet height unless <paramref name="creative"/> or
    /// <paramref name="inWater"/>; <paramref name="flying"/> and a step larger than
    /// <paramref name="maxStepBlocks"/> reset the peak to the current feet.
    /// </summary>
    public int Observe(float feet, bool grounded, bool flying, bool inWater, bool creative, float maxStepBlocks)
    {
        var damage = 0;
        LandedDrop = 0f;
        if (!_hasFallState)
        {
            _fallPeakFeet = feet;
            _prevFeet = feet;
            _wasGrounded = grounded;
            _hasFallState = true;
        }

        if (MathF.Abs(feet - _prevFeet) > maxStepBlocks)
        {
            _fallPeakFeet = feet;
            _wasGrounded = grounded;
        }
        else if (flying)
        {
            _fallPeakFeet = feet;
            _wasGrounded = grounded;
        }
        else if (!grounded)
        {
            if (_wasGrounded)
                _fallPeakFeet = MathF.Max(_fallPeakFeet, _prevFeet);
            _fallPeakFeet = MathF.Max(_fallPeakFeet, feet);
            _wasGrounded = false;
        }
        else if (!_wasGrounded)
        {
            var drop = _fallPeakFeet - feet;
            LandedDrop = MathF.Max(0f, drop);
            if (!creative && !inWater)
                damage = PlayerHealth.FallDamage(drop);
            _fallPeakFeet = feet;
            _wasGrounded = true;
        }
        else
        {
            _fallPeakFeet = feet;
        }
        _prevFeet = feet;
        return damage;
    }

    /// <summary>
    /// Restarts the tracking at <paramref name="feet"/> (a respawn or a server teleport), so
    /// the drop the body had built up before it is forgotten.
    /// </summary>
    public void Reset(float feet, bool grounded)
    {
        _fallPeakFeet = feet;
        _prevFeet = feet;
        _wasGrounded = grounded;
        _hasFallState = true;
        LandedDrop = 0f;
    }
}
