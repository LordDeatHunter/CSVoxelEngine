using System.Numerics;

namespace VoxelEngine.UI.Widgets;

/// <summary>
/// A scrolled list of equal-height rows seen through a viewport, GL-free: a thin wrapper over
/// <see cref="ScrollList"/> that speaks in canvas rectangles, so the draw, the hit-tests and the
/// scrollbar share one definition.
/// </summary>
public readonly record struct ScrollViewLayout(UiRect View, float RowHeight, int RowCount)
{
    public float ContentHeight => RowCount * RowHeight;

    public bool Overflows => ScrollList.Overflows(ContentHeight, View.Size.Y);

    public float Clamp(float offset) => ScrollList.Clamp(offset, ContentHeight, View.Size.Y);

    public (int First, int EndExclusive) VisibleRows(float offset) =>
        ScrollList.VisibleRange(offset, RowHeight, RowCount, View.Size.Y);

    /// <summary>The rectangle of <paramref name="row"/> after scrolling (it may lie outside the viewport).</summary>
    public UiRect RowRect(int row, float offset) =>
        UiRect.FromTopLeft(new Vector2(View.Left, View.Top + ScrollList.RowTop(row, offset, RowHeight)),
            new Vector2(View.Size.X, RowHeight));

    /// <summary>The row under <paramref name="point"/>, or null outside the viewport or past the last row.</summary>
    public int? RowAt(Vector2 point, float offset) =>
        point.X < View.Left || point.X >= View.Right
            ? null
            : ScrollList.RowAt(point.Y - View.Top, offset, RowHeight, RowCount, View.Size.Y);

    /// <summary>The offset nearest <paramref name="offset"/> that shows <paramref name="row"/> whole, for
    /// scroll-to-selection.</summary>
    public float OffsetToReveal(int row, float offset) =>
        Clamp(ScrollList.OffsetToReveal(row, offset, RowHeight, View.Size.Y));
}
