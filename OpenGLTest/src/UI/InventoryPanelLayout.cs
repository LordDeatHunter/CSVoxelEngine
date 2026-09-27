using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>What part of the inventory list's scrollbar a point lands on.</summary>
public enum ScrollbarHit
{
    None,
    Thumb,
    TrackAbove,
    TrackBelow,
}

/// <summary>
/// GL-free geometry of the [E] inventory panel's left half — the panel background, the tab
/// strip, the scrolled row list and its scrollbar — in canvas units (see <see cref="UiCanvas"/>).
/// Every method takes the canvas size, mirroring <see cref="SettingsLayout"/>, so the draw in
/// <see cref="UIRenderer"/> and the click handling in <c>Program</c> read one definition.
/// </summary>
public static class InventoryPanelLayout
{
    public const float PanelLeft = 40f;
    public const float PanelTop = 80f;
    public const float PanelGap = 16f;
    public const float PanelRightMargin = 24f;
    public const float PanelWidthMin = 480f;
    public const float PanelWidthMax = 660f;
    public const float PanelPad = 10f;
    public const float TabHeight = 60f;
    public const float RowHeight = 42f;

    /// <summary>Top of the row list viewport, just under the tab strip.</summary>
    public const float ListTop = PanelTop + PanelPad * 2f + TabHeight;

    /// <summary>Left edge of the row rectangles (and of their hit area).</summary>
    public const float RowLeft = PanelLeft + PanelPad;

    public const float ScrollbarWidth = 6f;
    /// <summary>Extra hit slack on each side of the thin track, for usability.</summary>
    public const float ScrollbarHitPad = 6f;
    public const float ThumbMinLength = 24f;
    /// <summary>Rows one mouse-wheel notch scrolls by.</summary>
    public const int WheelStepRows = 3;

    public static readonly InventoryCategory[] Tabs =
    [
        InventoryCategory.Blocks, InventoryCategory.Tools, InventoryCategory.Misc, InventoryCategory.Crafting,
    ];

    public static float PanelWidth(Vector2 screen) =>
        Math.Clamp((screen.X - PanelLeft - PanelGap - PanelRightMargin) * 0.58f,
            PanelWidthMin, PanelWidthMax);

    /// <summary>Panel height: from <see cref="PanelTop"/> down to a gap above the hotbar.</summary>
    public static float PanelHeight(Vector2 screen) =>
        Math.Max(RowHeight * 2f, HotbarLayout.Top(screen) - 14f - PanelTop);

    public static float TabWidth(Vector2 screen) => (PanelWidth(screen) - PanelPad * 2f) / Tabs.Length;

    public static Vector2 TabCentre(int t, Vector2 screen) =>
        new(PanelLeft + PanelPad + TabWidth(screen) * (t + 0.5f), PanelTop + PanelPad + TabHeight * 0.5f);

    /// <summary>
    /// Height of the list viewport. It ends <see cref="PanelPad"/> above the panel's bottom edge,
    /// so a partially visible last row is clipped inside the panel background.
    /// </summary>
    public static float ListHeight(Vector2 screen) =>
        MathF.Max(0f, PanelTop + PanelHeight(screen) - PanelPad - ListTop);

    public static float ContentHeight(int rowCount) => rowCount * RowHeight;

    public static float ClampedOffset(float offset, int rowCount, Vector2 screen) =>
        ScrollList.Clamp(offset, ContentHeight(rowCount), ListHeight(screen));

    public static float RowCentreY(int row, float offset, Vector2 screen) =>
        ListTop + ScrollList.RowTop(row, offset, RowHeight) + RowHeight * 0.5f;

    /// <summary>
    /// Right edge of the row hit area: the row rectangle's edge normally, the scrollbar hit band's
    /// left edge while the list overflows, so a point is never both a row and a scrollbar hit.
    /// </summary>
    public static float RowRight(Vector2 screen, int rowCount) =>
        Overflows(rowCount, screen)
            ? ScrollbarHitLeft(screen)
            : PanelLeft + PanelWidth(screen) - PanelPad;

    /// <summary>Panel tab under a canvas point, if any.</summary>
    public static InventoryCategory? TabAt(Vector2 point, Vector2 screen)
    {
        var width = PanelWidth(screen);
        if (point.X < PanelLeft || point.X > PanelLeft + width) return null;
        if (point.Y < PanelTop + PanelPad || point.Y > PanelTop + PanelPad + TabHeight) return null;

        var t = (int)((point.X - PanelLeft - PanelPad) / TabWidth(screen));
        return t >= 0 && t < Tabs.Length ? Tabs[t] : null;
    }

    /// <summary>List row under a canvas point for a list scrolled by <paramref name="offset"/>, if any.</summary>
    public static int? RowAt(Vector2 point, Vector2 screen, float offset, int rowCount)
    {
        // Half-open [RowLeft, RowRight): the scrollbar band starts exactly at RowRight while the
        // list overflows, and the edge pixel must belong to one of the two, not both.
        if (point.X < RowLeft || point.X >= RowRight(screen, rowCount)) return null;
        return ScrollList.RowAt(point.Y - ListTop, offset, RowHeight, rowCount, ListHeight(screen));
    }

    /// <summary>Whether a canvas point is over the list viewport (the wheel scrolls it there).</summary>
    public static bool IsOverList(Vector2 point, Vector2 screen) =>
        point.X >= PanelLeft && point.X <= PanelLeft + PanelWidth(screen)
        && point.Y >= ListTop && point.Y < ListTop + ListHeight(screen);

    /// <summary>The track runs down the middle of the panel's right padding.</summary>
    public static float TrackCentreX(Vector2 screen) => PanelLeft + PanelWidth(screen) - PanelPad * 0.5f;

    public static float ScrollbarHitLeft(Vector2 screen) =>
        TrackCentreX(screen) - ScrollbarWidth * 0.5f - ScrollbarHitPad;

    public static (Vector2 Centre, Vector2 Size) TrackRect(Vector2 screen)
    {
        var height = ListHeight(screen);
        return (new Vector2(TrackCentreX(screen), ListTop + height * 0.5f), new Vector2(ScrollbarWidth, height));
    }

    public static (Vector2 Centre, Vector2 Size) ThumbRect(Vector2 screen, float offset, int rowCount)
    {
        var view = ListHeight(screen);
        var (start, length) = ScrollList.Thumb(view, ContentHeight(rowCount), view, offset, ThumbMinLength);
        return (new Vector2(TrackCentreX(screen), ListTop + start + length * 0.5f),
            new Vector2(ScrollbarWidth, length));
    }

    /// <summary>Scrollbar part under a canvas point; always None while the list fits.</summary>
    public static ScrollbarHit ScrollbarHitAt(Vector2 point, Vector2 screen, float offset, int rowCount)
    {
        if (!Overflows(rowCount, screen)) return ScrollbarHit.None;
        var right = TrackCentreX(screen) + ScrollbarWidth * 0.5f + ScrollbarHitPad;
        if (point.X < ScrollbarHitLeft(screen) || point.X > right) return ScrollbarHit.None;
        if (point.Y < ListTop || point.Y > ListTop + ListHeight(screen)) return ScrollbarHit.None;

        var (centre, size) = ThumbRect(screen, offset, rowCount);
        var thumbTop = centre.Y - size.Y * 0.5f;
        if (point.Y < thumbTop) return ScrollbarHit.TrackAbove;
        if (point.Y > thumbTop + size.Y) return ScrollbarHit.TrackBelow;
        return ScrollbarHit.Thumb;
    }

    /// <summary>The (clamped) scroll offset that puts the thumb's top edge at canvas Y <paramref name="thumbTop"/>.</summary>
    public static float OffsetForThumbTop(float thumbTop, int rowCount, Vector2 screen)
    {
        var view = ListHeight(screen);
        return ScrollList.OffsetForThumbStart(thumbTop - ListTop, view, ContentHeight(rowCount), view, ThumbMinLength);
    }

    private static bool Overflows(int rowCount, Vector2 screen) =>
        ScrollList.Overflows(ContentHeight(rowCount), ListHeight(screen));
}
