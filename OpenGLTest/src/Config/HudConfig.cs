namespace VoxelEngine.Config;

/// <summary>
/// HUD and overlay tuning: the selection outline and its pulse, the loading screen, and the
/// hotbar's name fade, pickup pop and flash.
/// </summary>
public static class HudConfig
{
    /// <summary>
    /// Selection outline beam thickness per block of camera distance, so the outline keeps a
    /// roughly constant on-screen width (about 2 px at 1080p with the default FOV).
    /// </summary>
    public const float SelectionOutlinePixelScale = 0.0025f;

    /// <summary>Floor on the selection outline beam thickness, in blocks, for a box right at the eye.</summary>
    public const float SelectionOutlineMinThickness = 0.002f;

    /// <summary>
    /// Lowest opacity the selection outline breathes down to (SelectionPulse, selection-fragment.glsl's
    /// uPulseDepth = 1 - this); the top of each breath is full opacity. 1 switches the pulse off.
    /// </summary>
    public const float SelectionPulseMinOpacity = 0.7f;

    /// <summary>Seconds per breath of the selection outline (uPulsePeriod).</summary>
    public const float SelectionPulsePeriodSeconds = 1.6f;

    // Per-second rate the loading screen's shown bar eases toward the real load progress (MathUtils.Damp).
    public const float LoadingProgressDampRate = 6f;
    // Seconds of wall time for one revolution of the loading screen's spinner.
    public const double LoadingSpinnerPeriodSeconds = 1.2;
    // The loading overlay fades out over this many seconds once the world plays.
    public const float LoadingFadeSeconds = 0.5f;
    // Dots around the loading screen's spinner ring.
    public const int LoadingSpinnerDots = 8;

    // --- HUD feedback (HotbarNameFade, HotbarPickupPop) ---
    // Selecting a hotbar slot (or the selected slot changing item) shows the item's name above
    // the hotbar: fully opaque for the hold, then faded out, about 1.5 s in all.
    public const float HotbarNameHoldSeconds = 1.0f;
    public const float HotbarNameFadeSeconds = 0.5f;
    // A slot whose count rises (a pickup, a craft result, a server snapshot) bumps its icon to
    // this scale and springs back over the duration. The hotbar's icon fill leaves room for it.
    public const float HotbarPopSeconds = 0.3f;
    public const float HotbarPopScale = 1.3f;

    // How long a broken tool's hotbar slot flashes, and the strength of its white overlay at the start
    public const float HotbarFlashSeconds = 0.35f;
    public const float HotbarFlashAlpha = 0.6f;
}
