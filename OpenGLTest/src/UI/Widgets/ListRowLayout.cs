using System;
using System.Numerics;

namespace VoxelEngine.UI.Widgets;

/// <summary>
/// One list row: an icon at the left, the name after it and a right-aligned count, with the name given
/// only the width the count leaves. GL-free; positions and the name budget come from here, never from
/// the renderer.
/// </summary>
public readonly record struct ListRowLayout(UiRect Row, float IconSize)
{
    private static float Pad => UiTheme.Space.Md;

    public UiRect Icon => new(new Vector2(Row.Left + Pad + IconSize * 0.5f, Row.Center.Y), new Vector2(IconSize));

    /// <summary>Left edge of the name text.</summary>
    public float NameLeft => Icon.Right + UiTheme.Space.Sm;

    /// <summary>The right edge a count ends at.</summary>
    public float CountRight => Row.Right - Pad;

    /// <summary>Left edge of a right-aligned count of <paramref name="countWidth"/>.</summary>
    public float CountLeft(float countWidth) => CountRight - countWidth;

    /// <summary>The width the name may use before it meets the count (never negative; with no count it
    /// runs to the right padding).</summary>
    public float NameWidth(float countWidth) =>
        MathF.Max(0f, (countWidth > 0f ? CountLeft(countWidth) - UiTheme.Space.Sm : CountRight) - NameLeft);
}
