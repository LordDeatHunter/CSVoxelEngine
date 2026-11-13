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
    // Per-second rate the hearts, the boss bar and the mana bar ease toward their real values (MathUtils.Damp).
    public const float BarDampRate = 9f;
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
    // A name too wide for its box (the equipment card's) scrolls back and forth: it rests this long at each end,
    // and travels at MarqueeSpeed canvas pixels per second.
    public const float MarqueeHoldSeconds = 1.0f;
    public const float MarqueeSpeed = 30f;
    // A slot whose count rises (a pickup, a craft result, a server snapshot) bumps its icon to
    // this scale and springs back over the duration. The hotbar's icon fill leaves room for it.
    public const float HotbarPopSeconds = 0.3f;
    public const float HotbarPopScale = 1.3f;

    // How long a broken tool's hotbar slot flashes, and the strength of its white overlay at the start
    public const float HotbarFlashSeconds = 0.35f;
    public const float HotbarFlashAlpha = 0.6f;

    // --- UI animations (PanelTransition, PickupFlyIcons) ---
    // The [E] panel fades and slides up into place over the open time and fades away over the
    // shorter close time; the slide is how many canvas pixels it starts below its place.
    public const float PanelOpenSeconds = 0.18f;
    public const float PanelCloseSeconds = 0.12f;
    public const float PanelSlidePixels = 28f;

    // A collected drop's icon arcs from where the drop was into its hotbar slot over this long,
    // bowed up by the arc height at mid-flight and shrinking to the end scale on arrival.
    public const float PickupFlySeconds = 0.4f;
    public const float PickupFlyArcPixels = 60f;
    public const float PickupFlyEndScale = 0.6f;

    // The sparkle trail behind an item dragged across the [E] panel (DragTrail): particles per canvas pixel the
    // pointer travels, their lifetime, square size, scatter around the pointer and drift (canvas pixels per second).
    public const int DragTrailMax = 48;
    public const float DragTrailSpawnPerPixel = 0.25f;
    public const float DragTrailLifeSeconds = 0.45f;
    public const float DragTrailSizePixels = 6f;
    public const float DragTrailSpreadPixels = 14f;
    public const float DragTrailDriftPixelsPerSecond = 30f;
    public const float DragTrailFallPixelsPerSecond = 24f;
    public const byte DragTrailAlpha = 200;

    // The player's active status effects (HudRenderer.DrawEffects): one icon per effect, this many
    // canvas pixels square, in a row beside the hearts with this gap between icons and before them.
    public const float EffectIconSize = 28f;
    public const float EffectIconGap = 6f;
}
