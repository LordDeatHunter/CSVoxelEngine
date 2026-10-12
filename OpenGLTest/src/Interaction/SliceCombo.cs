namespace silkgltriangle;

/// <summary>
/// The blade's two-phase slice combo, GL-free so the punch loop and the tests share it. A press
/// starts a right-to-left swing; a press while one is playing buffers the opposite return swing,
/// which starts (at half power) when the current one finishes. The animation itself lives in
/// <see cref="PlayerModel"/>; this only decides which swing is active and the power it carries.
/// </summary>
public sealed class SliceCombo
{
    private bool _queued;

    /// <summary>Whether a swing is currently playing.</summary>
    public bool Active { get; private set; }

    /// <summary>Whether the active swing is the combo's return (left-to-right, half power).</summary>
    public bool ReturnSwing { get; private set; }

    /// <summary>+1 for a right-to-left swing, -1 for the left-to-right return; 0 while idle.</summary>
    public int Direction { get; private set; }

    /// <summary>
    /// Feeds one left-click. Starts a fresh right-to-left swing when idle, returning true; while
    /// a swing is playing it buffers the return instead, returning false.
    /// </summary>
    public bool Press()
    {
        if (Active)
        {
            _queued = true;
            return false;
        }

        Begin(returnSwing: false);
        return true;
    }

    /// <summary>
    /// Ends the finished swing and starts the buffered opposite swing if one was queued (so the
    /// directions alternate). Returns true when a new swing started, false when the combo went
    /// idle.
    /// </summary>
    public bool Advance()
    {
        if (!Active) return false;

        if (_queued)
        {
            _queued = false;
            Begin(returnSwing: !ReturnSwing);
            return true;
        }

        Active = false;
        ReturnSwing = false;
        Direction = 0;
        return false;
    }

    /// <summary>Drops any in-flight swing and buffered input (the item was switched away).</summary>
    public void Reset()
    {
        _queued = false;
        Active = false;
        ReturnSwing = false;
        Direction = 0;
    }

    private void Begin(bool returnSwing)
    {
        Active = true;
        ReturnSwing = returnSwing;
        Direction = returnSwing ? -1 : 1;
    }
}
