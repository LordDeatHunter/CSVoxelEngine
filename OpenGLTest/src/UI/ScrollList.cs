using System;

namespace VoxelEngine.UI;

/// <summary>
/// GL-free math for a vertically scrolled list of equal-height rows shown through a viewport
/// <c>viewHeight</c> tall, scrolled down by <c>offset</c> pixels. Everything is in canvas units
/// (see <see cref="UiCanvas"/>); row positions are relative to the viewport's top edge. Shared by
/// the inventory panel's draw, its hit-tests and its scrollbar so the three always agree.
/// </summary>
public static class ScrollList
{
    /// <summary>How far the list can scroll: the content height that does not fit, never negative.</summary>
    public static float MaxOffset(float contentHeight, float viewHeight) =>
        MathF.Max(0f, contentHeight - viewHeight);

    /// <summary>Clamps an offset to [0, <see cref="MaxOffset"/>].</summary>
    public static float Clamp(float offset, float contentHeight, float viewHeight) =>
        Math.Clamp(offset, 0f, MaxOffset(contentHeight, viewHeight));

    /// <summary>
    /// Whether the content is taller than the viewport, with half a pixel of slack so float noise
    /// never shows a scrollbar for a list that fits.
    /// </summary>
    public static bool Overflows(float contentHeight, float viewHeight) =>
        contentHeight > viewHeight + 0.5f;

    /// <summary>Top edge of <paramref name="row"/> relative to the viewport top after scrolling.</summary>
    public static float RowTop(int row, float offset, float rowHeight) => row * rowHeight - offset;

    /// <summary>
    /// The row under a point <paramref name="localY"/> pixels below the viewport top, or null when
    /// the point is outside the viewport or past the last row.
    /// </summary>
    public static int? RowAt(float localY, float offset, float rowHeight, int rowCount, float viewHeight)
    {
        if (localY < 0f || localY >= viewHeight || rowHeight <= 0f) return null;
        var row = (int)MathF.Floor((localY + offset) / rowHeight);
        return row >= 0 && row < rowCount ? row : null;
    }

    /// <summary>
    /// The rows at least partly inside the viewport, as a half-open range; empty for no rows.
    /// </summary>
    public static (int First, int EndExclusive) VisibleRange(float offset, float rowHeight, int rowCount, float viewHeight)
    {
        if (rowCount <= 0 || rowHeight <= 0f) return (0, 0);
        var first = Math.Clamp((int)MathF.Floor(offset / rowHeight), 0, rowCount);
        var end = Math.Clamp((int)MathF.Ceiling((offset + viewHeight) / rowHeight), first, rowCount);
        return (first, end);
    }

    /// <summary>
    /// Scrollbar thumb along a track <paramref name="trackLength"/> long: its length is the visible
    /// fraction of the content (at least <paramref name="minLength"/>, at most the track) and its
    /// start travels the rest of the track in proportion to the scroll. A list that fits gets a
    /// thumb filling the whole track.
    /// </summary>
    public static (float Start, float Length) Thumb(float trackLength, float contentHeight, float viewHeight,
        float offset, float minLength)
    {
        if (!Overflows(contentHeight, viewHeight)) return (0f, trackLength);
        var length = ThumbLength(trackLength, contentHeight, viewHeight, minLength);
        var max = MaxOffset(contentHeight, viewHeight);
        var start = (trackLength - length) * (Clamp(offset, contentHeight, viewHeight) / max);
        return (start, length);
    }

    /// <summary>
    /// Inverse of <see cref="Thumb"/>: the (clamped) offset that puts the thumb's start at
    /// <paramref name="thumbStart"/> along the track, used while dragging it.
    /// </summary>
    public static float OffsetForThumbStart(float thumbStart, float trackLength, float contentHeight,
        float viewHeight, float minLength)
    {
        var travel = trackLength - ThumbLength(trackLength, contentHeight, viewHeight, minLength);
        if (travel <= 0f) return 0f;
        return Clamp(thumbStart / travel * MaxOffset(contentHeight, viewHeight), contentHeight, viewHeight);
    }

    private static float ThumbLength(float trackLength, float contentHeight, float viewHeight, float minLength)
    {
        if (contentHeight <= 0f) return trackLength;
        var length = MathF.Max(minLength, trackLength * viewHeight / contentHeight);
        return MathF.Min(length, trackLength);
    }
}
