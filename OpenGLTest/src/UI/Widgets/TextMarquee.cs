using System;

namespace VoxelEngine.UI.Widgets;

/// <summary>
/// GL-free scroll of a one-line text that is wider than its box: the text rests at its start, travels to
/// its end, rests, and travels back, over and over. Timing is <see cref="HudConfig.MarqueeHoldSeconds"/> and
/// <see cref="HudConfig.MarqueeSpeed"/>. The draw in <c>UIRenderer.DrawTextMarqueeY</c> clips to the box and
/// shifts the text by <see cref="Offset"/>.
/// </summary>
public static class TextMarquee
{
    /// <summary>
    /// How far the text is shifted left, in canvas pixels, at <paramref name="seconds"/>: 0 when it fits, and
    /// otherwise a value from 0 up to the overflow (<paramref name="textWidth"/> minus <paramref name="viewWidth"/>).
    /// </summary>
    public static float Offset(float textWidth, float viewWidth, float seconds)
    {
        var overflow = textWidth - viewWidth;
        if (overflow <= 0f) return 0f;

        var travel = overflow / HudConfig.MarqueeSpeed;
        var hold = HudConfig.MarqueeHoldSeconds;
        var cycle = 2f * (hold + travel);
        var t = seconds % cycle;
        if (t < 0f) t += cycle;

        if (t < hold) return 0f;
        if (t < hold + travel) return (t - hold) / travel * overflow;
        if (t < 2f * hold + travel) return overflow;
        return MathF.Max(0f, 1f - (t - 2f * hold - travel) / travel) * overflow;
    }
}
