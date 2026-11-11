using System;
using System.Numerics;

namespace VoxelEngine.UI.Widgets;

/// <summary>A single-line text field's geometry, GL-free: the text starts one padding in, is centered on
/// the box's middle, and may use the width left after both paddings and the caret.</summary>
public readonly record struct TextBoxLayout(UiRect Box)
{
    public const float CaretWidth = 2f;
    public const float CaretHeight = 22f;
    private const float CaretGap = 3f;

    private static float Pad => UiTheme.Space.Md;

    public float TextLeft => Box.Left + Pad;

    public float CenterY => Box.Center.Y;

    /// <summary>The widest text that fits with room for the caret after it (never negative).</summary>
    public float TextBudget => MathF.Max(0f, Box.Size.X - Pad * 2f - CaretGap * 2f);

    /// <summary>The caret rectangle after text of <paramref name="textWidth"/>.</summary>
    public UiRect Caret(float textWidth) =>
        new(new Vector2(TextLeft + textWidth + CaretGap, Box.Center.Y), new Vector2(CaretWidth, CaretHeight));
}
