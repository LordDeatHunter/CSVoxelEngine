namespace VoxelEngine.Player;

/// <summary>
/// The sprint latch, GL-free: with the sprint-toggle setting on, the sprint key flips the latch on
/// each fresh press instead of having to be held; off, the latch simply follows the key. The camera
/// owns one latch per player; a test drives this rule directly.
/// </summary>
public static class SprintToggle
{
    /// <summary>
    /// Advances the latch: the previous state plus this frame's key edge. In toggle mode a fresh
    /// press flips it (a held key does nothing more); otherwise it is the key's own held state.
    /// </summary>
    public static bool Step(bool latched, bool keyHeld, bool wasHeld, bool toggleMode) =>
        toggleMode ? (keyHeld && !wasHeld ? !latched : latched) : keyHeld;
}
