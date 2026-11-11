using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.UI;
using Xunit;

namespace VoxelEngine.Tests.UI;

public sealed class TextFitTests
{
    // Width grows with length and with scale, like any real font.
    private static float Mono(ReadOnlySpan<char> text, float scale) => text.Length * 10f * scale;

    private static readonly string[] Texts =
    [
        "Hi", "Obsidian Blade", "A long item name that cannot possibly fit", new string('x', 500),
    ];

    private static readonly float[] Widths = [1f, 15f, 60f, 140f, 400f, 6000f];

    private static IEnumerable<(string Text, float Width)> Sweep() =>
        from t in Texts from w in Widths select (t, w);

    [Fact]
    public void ALineThatFitsIsReturnedUntouched()
    {
        var fitted = TextFit.SingleLine("Hello", 1f, 1000f, Mono);
        Assert.Equal("Hello", fitted.Text);
        Assert.Equal(1f, fitted.Scale);
        Assert.False(fitted.Truncated);
    }

    [Fact]
    public void TheResultAlwaysFitsOnceThereIsRoomForTheEllipsis()
    {
        foreach (var (text, width) in Sweep())
        {
            var fitted = TextFit.SingleLine(text, 1f, width, Mono);
            var ellipsisWidth = Mono(TextFit.Ellipsis, fitted.Scale);
            if (width >= ellipsisWidth)
                Assert.True(Mono(fitted.Text, fitted.Scale) <= width, $"'{text}' at {width}");
        }
    }

    [Fact]
    public void ShrinkingNeverGoesBelowTheFloorAndNeverGrowsTheText()
    {
        foreach (var (text, width) in Sweep())
        {
            var fitted = TextFit.SingleLine(text, 2f, width, Mono, 0.5f);
            Assert.InRange(fitted.Scale, 1f, 2f);
        }
    }

    [Fact]
    public void TextIsShrunkBeforeItIsCut()
    {
        // 14 characters need 140 at scale 1; 120 is within the 0.7 floor, so nothing is dropped.
        var fitted = TextFit.SingleLine("Obsidian Blade", 1f, 120f, Mono);
        Assert.False(fitted.Truncated);
        Assert.Equal("Obsidian Blade", fitted.Text);
        Assert.True(fitted.Scale < 1f);
    }

    [Fact]
    public void ACutKeepsTheLongestHeadThatFits()
    {
        var fitted = TextFit.SingleLine(new string('x', 100), 1f, 100f, Mono, 1f);
        Assert.True(fitted.Truncated);
        Assert.EndsWith(TextFit.Ellipsis, fitted.Text);
        Assert.True(Mono(fitted.Text, fitted.Scale) <= 100f);
        var longer = new string('x', fitted.Text.Length - TextFit.Ellipsis.Length + 1) + TextFit.Ellipsis;
        Assert.True(Mono(longer, fitted.Scale) > 100f);
        Assert.StartsWith(fitted.Text[..^TextFit.Ellipsis.Length], new string('x', 100));
    }

    [Fact]
    public void ANarrowerWidthNeverKeepsMoreText()
    {
        var text = new string('x', 200);
        var previous = int.MaxValue;
        foreach (var width in new[] { 2000f, 800f, 300f, 120f, 60f, 20f })
        {
            var length = TextFit.SingleLine(text, 1f, width, Mono, 1f).Text.Length;
            Assert.True(length <= previous);
            previous = length;
        }
    }

    [Fact]
    public void NoWidthYieldsAnEmptyTruncatedLine()
    {
        var fitted = TextFit.SingleLine("Hello", 1f, 0f, Mono);
        Assert.Equal("", fitted.Text);
        Assert.True(fitted.Truncated);
    }

    [Fact]
    public void ARunLongerThanOneTextBatchIsMeasuredAndFittedWhole()
    {
        var text = new string('a', 5000);
        Assert.False(TextFit.SingleLine(text, 1f, 100000f, Mono).Truncated);
        var cut = TextFit.SingleLine(text, 1f, 1000f, Mono, 1f);
        Assert.True(cut.Truncated);
        Assert.True(cut.Text.Length > 64);
    }
}
