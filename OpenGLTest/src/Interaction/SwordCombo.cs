using System;
using VoxelEngine.Player;

namespace VoxelEngine.Interaction;

/// <summary>
/// The sword's three-swing chain, GL-free so the attack loop and the tests share it. A press
/// starts the cut; a press while a swing is playing buffers the next swing of the chain
/// (<see cref="SwordSwings.Next"/>), which starts when the current one finishes; and a swing that
/// ends with nothing buffered leaves the chain open for <see cref="ChainWindowSeconds"/>, so a press
/// inside that window still continues it (cut, back cut, chop) while a later press starts over at
/// the cut. The animation lives in <see cref="PlayerModel"/>; this only decides which swing is
/// active.
/// </summary>
public sealed class SwordCombo
{
    /// <summary>Seconds after a swing ends during which the next press continues the chain instead of
    /// starting it over.</summary>
    public const float ChainWindowSeconds = 0.35f;

    private bool _queued;
    private float _window;
    private SwordSwing _next = SwordSwing.Cut;

    /// <summary>Whether a swing is currently playing.</summary>
    public bool Active { get; private set; }

    /// <summary>The swing playing (meaningful while <see cref="Active"/>).</summary>
    public SwordSwing Swing { get; private set; }

    /// <summary>
    /// Feeds one left-click. Idle, it starts a swing and returns true: the next swing of the chain
    /// while the window after the last one is still open, else the cut. While a swing is playing it
    /// buffers the next swing instead and returns false.
    /// </summary>
    public bool Press()
    {
        if (Active)
        {
            _queued = true;
            return false;
        }

        Begin(_window > 0f ? _next : SwordSwing.Cut);
        return true;
    }

    /// <summary>
    /// Ends the finished swing: starts the buffered next swing if one was queued (returning true),
    /// otherwise goes idle with the chain window open (returning false).
    /// </summary>
    public bool Advance()
    {
        if (!Active) return false;

        var next = SwordSwings.Next(Swing);
        if (_queued)
        {
            _queued = false;
            Begin(next);
            return true;
        }

        Active = false;
        _next = next;
        _window = ChainWindowSeconds;
        return false;
    }

    /// <summary>Ages the chain window while idle; call once a frame.</summary>
    public void Update(float deltaTime)
    {
        if (Active || _window <= 0f || deltaTime <= 0f) return;
        _window = MathF.Max(0f, _window - deltaTime);
    }

    /// <summary>Drops any in-flight swing, buffered input and open window (the item was switched away).</summary>
    public void Reset()
    {
        _queued = false;
        _window = 0f;
        _next = SwordSwing.Cut;
        Active = false;
        Swing = SwordSwing.Cut;
    }

    private void Begin(SwordSwing swing)
    {
        Active = true;
        Swing = swing;
        _window = 0f;
    }
}
