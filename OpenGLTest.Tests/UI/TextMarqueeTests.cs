using System;
using VoxelEngine.UI.Widgets;
using Xunit;

namespace VoxelEngine.Tests.UI;

public sealed class TextMarqueeTests
{
    [Fact]
    public void TextThatFitsNeverMoves()
    {
        for (var t = 0f; t < 30f; t += 0.25f)
            Assert.Equal(0f, TextMarquee.Offset(80f, 120f, t));
    }

    [Fact]
    public void WideTextStaysWithinItsOverflowAndSweepsItAll()
    {
        const float text = 200f, view = 120f;
        var overflow = text - view;
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var t = 0f; t < 60f; t += 0.05f)
        {
            var offset = TextMarquee.Offset(text, view, t);
            min = MathF.Min(min, offset);
            max = MathF.Max(max, offset);
            Assert.InRange(offset, 0f, overflow);
        }
        // It reaches the full overflow (the end of the name is shown) and comes back to the start
        Assert.InRange(max, overflow - 0.5f, overflow);
        Assert.InRange(min, 0f, 0.5f);
    }

    [Fact]
    public void ScrollReturnsToTheStartOverAndOver()
    {
        const float text = 200f, view = 120f;
        // Sample one long span; the start rest must recur, so the first and a much later rest read 0 alike
        var early = TextMarquee.Offset(text, view, 0f);
        var sawRestAgain = false;
        for (var t = 20f; t < 60f; t += 0.05f)
            if (TextMarquee.Offset(text, view, t) == early)
                sawRestAgain = true;
        Assert.True(sawRestAgain);
    }
}
