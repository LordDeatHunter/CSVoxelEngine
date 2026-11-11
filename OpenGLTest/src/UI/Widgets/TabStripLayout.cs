using System;
using System.Numerics;

namespace VoxelEngine.UI.Widgets;

/// <summary>
/// A row of equal-width tabs with a gap between them, GL-free. The hit-test uses the very rectangles the
/// draw does, so a point in a gap belongs to no tab.
/// </summary>
public readonly record struct TabStripLayout(UiRect Strip, int Count, float Gap)
{
    public float TabWidth => Count <= 0 ? 0f : MathF.Max(0f, (Strip.Size.X - Gap * (Count - 1)) / Count);

    /// <summary>The rectangle of tab <paramref name="index"/>.</summary>
    public UiRect Tab(int index) =>
        UiRect.FromTopLeft(new Vector2(Strip.Left + index * (TabWidth + Gap), Strip.Top),
            new Vector2(TabWidth, Strip.Size.Y));

    /// <summary>The tab under <paramref name="point"/>, or -1.</summary>
    public int TabAt(Vector2 point)
    {
        for (var i = 0; i < Count; i++)
            if (Tab(i).Contains(point)) return i;
        return -1;
    }

    /// <summary>The underline's left edge and width for a (possibly fractional, mid-slide) tab position, so the
    /// underline can ease between tabs by interpolating its neighbors' rectangles.</summary>
    public (float Left, float Width) Underline(float position)
    {
        if (Count <= 0) return (Strip.Left, 0f);
        var clamped = Math.Clamp(position, 0f, Count - 1);
        var from = (int)MathF.Floor(clamped);
        var to = Math.Min(Count - 1, from + 1);
        var t = clamped - from;
        var a = Tab(from);
        var b = Tab(to);
        return (a.Left + (b.Left - a.Left) * t, TabWidth);
    }
}
