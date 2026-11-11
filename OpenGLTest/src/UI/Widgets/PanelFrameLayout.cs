using System;
using System.Numerics;

namespace VoxelEngine.UI.Widgets;

/// <summary>
/// The frame every panel and card shares: a border just inside the outer rectangle, a thin accent bar
/// along the top edge inside it, and the content area left once both and the padding are taken out.
/// GL-free; the renderer draws <see cref="Outer"/> as the border and <see cref="AccentBar"/> as a bar.
/// </summary>
public readonly record struct PanelFrameLayout(UiRect Outer, float BorderWidth, float AccentHeight, float Padding)
{
    /// <summary>The standard frame: the standard border, a 2px accent bar and medium padding.</summary>
    public static PanelFrameLayout Standard(UiRect outer) =>
        new(outer, UiTheme.Border.Standard, 2f, UiTheme.Space.Md);

    /// <summary>The accent bar: the full inner width, right under the top border.</summary>
    public UiRect AccentBar =>
        UiRect.FromTopLeft(new Vector2(Outer.Left + BorderWidth, Outer.Top + BorderWidth),
            new Vector2(MathF.Max(0f, Outer.Size.X - BorderWidth * 2f), AccentHeight));

    /// <summary>Where the panel's own content goes: inside the border, below the accent bar, padded.</summary>
    public UiRect Content
    {
        get
        {
            var left = Outer.Left + BorderWidth + Padding;
            var top = Outer.Top + BorderWidth + AccentHeight + Padding;
            var width = MathF.Max(0f, Outer.Right - BorderWidth - Padding - left);
            var height = MathF.Max(0f, Outer.Bottom - BorderWidth - Padding - top);
            return UiRect.FromTopLeft(new Vector2(left, top), new Vector2(width, height));
        }
    }
}
