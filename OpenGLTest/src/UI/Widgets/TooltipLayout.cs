using System;
using System.Collections.Generic;
using System.Numerics;

namespace VoxelEngine.UI.Widgets;

/// <summary>A laid-out tooltip: its box on the canvas, the lines to draw (already wrapped) and whether
/// lines were dropped to fit the line cap.</summary>
public readonly record struct TooltipBox(UiRect Box, IReadOnlyList<string> Lines, bool Clamped);

/// <summary>
/// Tooltip geometry, GL-free against an injected measure: each source line wraps to a maximum width, the
/// whole text is clamped to a line count (the last kept line ends in an ellipsis), the box hugs the text
/// and is placed beside the anchor, flipped or pushed so it never leaves the canvas.
/// </summary>
public static class TooltipLayout
{
    /// <summary>Offset of the box from the anchor (the pointer).</summary>
    public static readonly Vector2 AnchorOffset = new(16f, 18f);

    public static TooltipBox Compute(IReadOnlyList<string> sourceLines, Vector2 anchor, Vector2 canvas,
        float maxTextWidth, int maxLines, float lineHeight, TextMeasure measure, float footerHeight = 0f)
    {
        var padding = UiTheme.Space.Md;
        var lines = new List<string>();
        foreach (var source in sourceLines)
            TextWrap.Wrap(source, maxTextWidth, measure, lines);

        var clamped = maxLines > 0 && lines.Count > maxLines;
        if (clamped)
        {
            lines.RemoveRange(maxLines, lines.Count - maxLines);
            lines[^1] = TextFit.SingleLine(lines[^1] + TextFit.Ellipsis, 1f, maxTextWidth,
                (text, _) => measure(text), minScaleShare: 1f).Text;
        }

        var textWidth = 0f;
        foreach (var line in lines)
            textWidth = MathF.Max(textWidth, measure(line));

        var size = new Vector2(textWidth + padding * 2f, lines.Count * lineHeight + padding * 2f + footerHeight);
        var topLeft = anchor + AnchorOffset;
        if (topLeft.X + size.X > canvas.X) topLeft.X = anchor.X - AnchorOffset.X - size.X;
        if (topLeft.Y + size.Y > canvas.Y) topLeft.Y = anchor.Y - AnchorOffset.Y - size.Y;
        topLeft.X = Math.Clamp(topLeft.X, 0f, MathF.Max(0f, canvas.X - size.X));
        topLeft.Y = Math.Clamp(topLeft.Y, 0f, MathF.Max(0f, canvas.Y - size.Y));
        return new TooltipBox(UiRect.FromTopLeft(topLeft, size), lines, clamped);
    }
}
