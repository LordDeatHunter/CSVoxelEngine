namespace VoxelEngine.Config;

/// <summary>
/// Quest tuning: the completion toast's timing and size, and the quest book's grid spacing, zoom range,
/// node sizes and drag threshold.
/// </summary>
public static class QuestConfig
{
    // --- Completion toast (QuestToastQueue, QuestToastRenderer) ---
    // A card slides in from the right edge, holds, then slides back out; the next queued card follows
    public const float ToastSlideInSeconds = 0.35f;
    public const float ToastHoldSeconds = 3.5f;
    public const float ToastSlideOutSeconds = 0.3f;
    // The alpha fades in over the start of the slide-in and out over the whole slide-out
    public const float ToastFadeInSeconds = 0.2f;
    // Card size and its gap from the bottom-right canvas corner, in canvas units
    public const float ToastWidth = 500f;
    public const float ToastHeight = 84f;
    public const float ToastMarginRight = 28f;
    public const float ToastMarginBottom = 28f;

    // --- Quest book (QuestBookLayout) ---
    // Canvas units between two grid steps at zoom 1 (a quest's pos is in grid steps)
    public const float GridSpacing = 72f;
    // Node diameter at zoom 1, and the item icon's size inside it
    public const float NodeSize = 56f;
    public const float NodeIconSize = 34f;
    // Zoom range and the factor one wheel notch multiplies the zoom by
    public const float MinZoom = 0.4f;
    public const float MaxZoom = 2.5f;
    public const float ZoomStep = 1.15f;
    // Canvas units the pointer travels with the button down before a press becomes a pan instead of a click
    public const float DragThreshold = 6f;
    // Edge thickness at zoom 1
    public const float EdgeThickness = 4f;
    // The details panel's width, in canvas units
    public const float DetailsWidth = 440f;
}
