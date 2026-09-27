using System;
using System.Globalization;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The UI's virtual canvas, modelled on Godot's "canvas_items / keep aspect / expand" stretch mode:
/// the UI is laid out against <see cref="ReferenceSize"/> and scaled uniformly to the window.
/// <b>Canvas space</b> is what every <c>UIRenderer</c> draw call and layout constant is measured in;
/// window pixels only appear at the edges (mouse input in, the scissor rectangle out).
/// At the fit scale (<see cref="ScaleFor"/>) the canvas is at least the reference size on both axes,
/// equal to it on the limiting axis and expanded on the other, so it keeps the window's aspect and
/// edge-anchored HUD stays on the window edges. GL-free.
/// </summary>
public static class UiCanvas
{
    /// <summary>The resolution the UI layout constants were authored against.</summary>
    public static readonly Vector2 ReferenceSize = new(1920f, 1080f);

    /// <summary>The fit scale (window pixels per canvas unit): the largest uniform scale at which the
    /// reference size still fits the window on both axes. Any non-positive input returns 1.</summary>
    public static float ScaleFor(Vector2 windowSize, Vector2 referenceSize)
    {
        if (windowSize.X <= 0f || windowSize.Y <= 0f || referenceSize.X <= 0f || referenceSize.Y <= 0f)
            return 1f;
        return MathF.Min(windowSize.X / referenceSize.X, windowSize.Y / referenceSize.Y);
    }

    /// <summary>The canvas size a window of <paramref name="windowSize"/> exposes at
    /// <paramref name="scale"/>; a non-positive scale is treated as 1.</summary>
    public static Vector2 CanvasSizeFor(Vector2 windowSize, float scale) => windowSize / Safe(scale);

    /// <summary>Window pixels -> canvas units.</summary>
    public static Vector2 WindowToCanvas(Vector2 windowPoint, float scale) => windowPoint / Safe(scale);

    /// <summary>Canvas units -> window pixels.</summary>
    public static Vector2 CanvasToWindow(Vector2 canvasPoint, float scale) => canvasPoint * Safe(scale);

    /// <summary>
    /// Converts a canvas rectangle (top-left origin) into a <c>glScissor</c> rectangle: framebuffer
    /// pixels with GL's bottom-left origin. The window -> framebuffer ratio is applied per axis, so a
    /// HiDPI framebuffer larger than the window is handled. Edges are rounded outward so a row edge is
    /// never shaved, and the result is clamped to the framebuffer; width and height are never negative.
    /// </summary>
    public static (int X, int Y, int Width, int Height) ScissorRect(
        Vector2 canvasTopLeft, Vector2 canvasSize, float scale, Vector2 windowSize, Vector2 framebufferSize)
    {
        scale = Safe(scale);
        float kx = windowSize.X > 0f ? framebufferSize.X / windowSize.X : 1f;
        float ky = windowSize.Y > 0f ? framebufferSize.Y / windowSize.Y : 1f;

        float leftPx = canvasTopLeft.X * scale * kx;
        float topPx = canvasTopLeft.Y * scale * ky;
        float rightPx = leftPx + canvasSize.X * scale * kx;
        float bottomPx = topPx + canvasSize.Y * scale * ky;

        int fbW = Math.Max(0, (int)framebufferSize.X);
        int fbH = Math.Max(0, (int)framebufferSize.Y);
        int xMin = Math.Clamp((int)MathF.Floor(leftPx), 0, fbW);
        int xMax = Math.Clamp((int)MathF.Ceiling(rightPx), 0, fbW);
        int yTop = Math.Clamp((int)MathF.Floor(topPx), 0, fbH);
        int yBot = Math.Clamp((int)MathF.Ceiling(bottomPx), 0, fbH);
        xMax = Math.Max(xMax, xMin);
        yBot = Math.Max(yBot, yTop);

        return (xMin, fbH - yBot, xMax - xMin, yBot - yTop);
    }

    /// <summary>
    /// The GUI Scale policy. <paramref name="requestedScale"/> null (or non-positive) is Auto: the fit
    /// scale, scaling both up and down. A fixed multiplier of the reference layout is honoured exactly
    /// while it fits and capped at the fit scale otherwise, like Minecraft refusing a GUI scale that
    /// does not fit, so the UI can never leave the window.
    /// </summary>
    public static float EffectiveScale(float? requestedScale, Vector2 windowSize, Vector2 referenceSize)
    {
        var fit = ScaleFor(windowSize, referenceSize);
        return requestedScale is { } r && r > 0f ? MathF.Min(r, fit) : fit;
    }

    /// <summary>A scale as the settings page shows it, e.g. "1.25x"; shared by the option labels and
    /// the capped suffix so the two never disagree.</summary>
    public static string FormatScale(float scale) => scale.ToString("0.##", CultureInfo.InvariantCulture) + "x";

    /// <summary>The GUI Scale row's displayed value: "Auto", the pick itself while it fits, or the pick
    /// followed by the effective scale in parentheses while it is capped (e.g. "2x (1.33x)").</summary>
    public static string ScaleLabel(float? requestedScale, float effectiveScale)
    {
        if (requestedScale is not { } requested) return "Auto";
        return effectiveScale >= requested - 1e-3f
            ? FormatScale(requested)
            : $"{FormatScale(requested)} ({FormatScale(effectiveScale)})";
    }

    private static float Safe(float scale) => scale > 0f ? scale : 1f;
}
