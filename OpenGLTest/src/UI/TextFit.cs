using System;

namespace VoxelEngine.UI;

/// <summary>What <see cref="TextFit.SingleLine"/> decided for one line of text.</summary>
/// <param name="Text">The text to draw: the original, or its head with an ellipsis.</param>
/// <param name="Scale">The draw scale to use: the requested one, or shrunk toward the floor.</param>
/// <param name="Truncated">Whether characters were dropped, so the full text belongs in a hover tooltip.</param>
public readonly record struct FittedLine(string Text, float Scale, bool Truncated);

/// <summary>
/// Single-line text fitting, GL-free against an injected measure: shrink the text first, and only
/// when the floor scale still does not fit cut it with an ellipsis. Wrapping is <see cref="TextWrap"/>;
/// a label that cannot wrap uses this and reports whether it had to cut (<see cref="FittedLine.Truncated"/>).
/// </summary>
public static class TextFit
{
    /// <summary>ASCII dots, because the font only covers printable ASCII (U+2026 would vanish).</summary>
    public const string Ellipsis = "...";

    /// <summary>The share of the requested scale a line may shrink to before it is cut instead.</summary>
    public const float DefaultMinScaleShare = 0.7f;

    /// <summary>Width in canvas units of <paramref name="text"/> at the scale <see cref="SingleLine"/> was given.</summary>
    public delegate float ScaledMeasure(ReadOnlySpan<char> text, float scale);

    /// <summary>
    /// Fits <paramref name="text"/> into <paramref name="maxWidth"/>: unchanged when it fits at
    /// <paramref name="scale"/>, else shrunk linearly (never below <paramref name="scale"/> times
    /// <paramref name="minScaleShare"/>), else cut at the floor scale to the longest head whose text plus
    /// <see cref="Ellipsis"/> fits (the ellipsis alone when even one character does not). A width
    /// of zero or less yields an empty, truncated line.
    /// </summary>
    public static FittedLine SingleLine(string text, float scale, float maxWidth, ScaledMeasure measure,
        float minScaleShare = DefaultMinScaleShare)
    {
        if (string.IsNullOrEmpty(text))
            return new FittedLine(text ?? "", scale, false);
        if (maxWidth <= 0f)
            return new FittedLine("", scale, true);

        var width = measure(text, scale);
        if (width <= maxWidth)
            return new FittedLine(text, scale, false);

        var floor = scale * Math.Clamp(minScaleShare, 0.01f, 1f);
        var shrunk = Math.Max(floor, scale * maxWidth / width);
        if (measure(text, shrunk) <= maxWidth)
            return new FittedLine(text, shrunk, false);

        // Binary search the longest head that fits with the dots: the width grows with the length.
        var lo = 0;
        var hi = text.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (measure(string.Concat(text.AsSpan(0, mid), Ellipsis), shrunk) <= maxWidth)
                lo = mid;
            else
                hi = mid - 1;
        }

        return new FittedLine(string.Concat(text.AsSpan(0, lo), Ellipsis), shrunk, true);
    }
}
