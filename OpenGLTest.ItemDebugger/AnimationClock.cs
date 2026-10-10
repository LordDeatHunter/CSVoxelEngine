using System;

namespace silkgltriangle;

/// <summary>
/// What an <see cref="AnimationClock"/> drives: a preview that can be restarted from a known state and
/// stepped forward, and that knows how long one play of its current animation is.
/// </summary>
public interface IPreviewDriver
{
    /// <summary>Seconds one play of the current animation lasts (always above zero).</summary>
    float Duration { get; }

    /// <summary>True for an animation with no start or end of its own (idle, walk, swim): the clock wraps its
    /// time instead of restarting the simulation, so a loop never shows the warm-up again.</summary>
    bool IsContinuous { get; }

    /// <summary>Whether the preview replays a one-shot by itself. The clock turns it off and owns the replay.</summary>
    bool Replay { get; set; }

    /// <summary>Returns to the exact state the current animation starts from, so stepping it forward again
    /// reproduces the same frames.</summary>
    void Restart();

    /// <summary>Steps the preview forward by <paramref name="deltaTime"/> seconds.</summary>
    void Update(float deltaTime);
}

/// <summary>
/// The playback controls of the item debugger's timeline: how far through the animation it is
/// (<see cref="Time"/>, which a slider can scrub), how fast it plays (<see cref="Speed"/>, x0 to x4 in tenths)
/// and whether it loops. GL-free and tested. The driver is only ever stepped in fixed
/// <see cref="Step"/>-second slices, so seeking to a time (restart, then step up to it) lands on exactly the frame
/// that playing there would, whatever the frame rate was.
/// </summary>
public sealed class AnimationClock
{
    /// <summary>Fastest playback.</summary>
    public const float MaxSpeed = 4f;

    /// <summary>The increment of <see cref="Speed"/>.</summary>
    public const float SpeedStep = 0.1f;

    /// <summary>The fixed slice the driver is stepped in.</summary>
    public const float Step = 1f / 60f;

    private IPreviewDriver? _driver;
    private float _speed = 1f;

    /// <summary>Whether to start over at the end (on) or hold the final pose there (off).</summary>
    public bool Loop { get; set; } = true;

    /// <summary>True while the progress slider is held: time stops advancing and the slider owns it.</summary>
    public bool Scrubbing { get; set; }

    /// <summary>Seconds into the current play, 0..<see cref="Duration"/>.</summary>
    public float Time { get; private set; }

    /// <summary>Playback speed: 0 (paused) to <see cref="MaxSpeed"/>, in steps of <see cref="SpeedStep"/>.</summary>
    public float Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(MathF.Round(value / SpeedStep) * SpeedStep, 0f, MaxSpeed);
    }

    /// <summary>Length of the current play, from the driver.</summary>
    public float Duration => _driver is null ? 1f : MathF.Max(_driver.Duration, Step);

    /// <summary>How far through the play it is, 0..1.</summary>
    public float Progress => Duration > 0f ? Math.Clamp(Time / Duration, 0f, 1f) : 0f;

    /// <summary>True once a non-looping play has reached its end and is holding the last pose.</summary>
    public bool Finished => !Loop && Time >= Duration - 1e-4f;

    /// <summary>Points the clock at <paramref name="driver"/> and starts it from the beginning.</summary>
    public void Attach(IPreviewDriver driver)
    {
        _driver = driver;
        driver.Replay = false;
        Reset();
    }

    /// <summary>Starts the current animation over: the driver back to its start state, time zero.</summary>
    public void Reset()
    {
        _driver?.Restart();
        Time = 0f;
        _carry = 0f;
    }

    private float _carry;

    /// <summary>Advances by <paramref name="deltaTime"/> real seconds at the current speed (nothing while scrubbing or paused).</summary>
    public void Update(float deltaTime)
    {
        if (_driver is null || Scrubbing || deltaTime <= 0f || _speed <= 0f) return;

        // Whole fixed slices only: the remainder waits for the next frame, so the driver never sees an odd step.
        _carry += deltaTime * _speed;
        while (_carry >= Step)
        {
            _carry -= Step;
            if (!StepOnce()) { _carry = 0f; return; }
        }
    }

    // One fixed step; false when the play ended and is not looping.
    private bool StepOnce()
    {
        var duration = Duration;
        if (Time >= duration - 1e-4f)
        {
            if (!Loop) return false;
            Wrap();
        }
        _driver!.Update(Step);
        Time += Step;
        return true;
    }

    private void Wrap()
    {
        if (_driver!.IsContinuous)
            Time = 0f;
        else
            Reset();
    }

    /// <summary>Jumps to <paramref name="seconds"/> into the play: restart, then step to it in fixed slices.</summary>
    public void Seek(float seconds)
    {
        if (_driver is null) return;
        var target = Math.Clamp(seconds, 0f, Duration);
        _driver.Restart();
        Time = 0f;
        _carry = 0f;
        while (Time + Step <= target + 1e-4f)
        {
            _driver.Update(Step);
            Time += Step;
        }
    }
}
