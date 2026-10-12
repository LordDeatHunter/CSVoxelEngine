using System;
using VoxelEngine.UI;
using Xunit;

namespace VoxelEngine.Tests.UI;

public sealed class ScrollListTests
{
    private const float Eps = 1e-3f;
    private const float RowHeight = 40f;
    private const float View = 300f;
    private const float Track = 300f;
    private const float MinThumb = 24f;

    [Fact]
    public void OffsetClampsToTheOverflow()
    {
        var fits = View * 0.5f;
        Assert.Equal(0f, ScrollList.MaxOffset(fits, View));
        Assert.Equal(0f, ScrollList.MaxOffset(View, View));
        Assert.Equal(0f, ScrollList.Clamp(123f, fits, View));
        Assert.Equal(0f, ScrollList.Clamp(-5f, fits, View));

        var content = View * 3f;
        Assert.Equal(0f, ScrollList.Clamp(-10f, content, View));
        Assert.Equal(content - View, ScrollList.Clamp(1e6f, content, View));
        var inRange = (content - View) / 3f;
        Assert.Equal(inRange, ScrollList.Clamp(inRange, content, View));
    }

    [Fact]
    public void ThumbLengthIsTheVisibleFraction()
    {
        var content = View * 4f;
        var (_, length) = ScrollList.Thumb(Track, content, View, 0f, MinThumb);
        Assert.Equal(View / content, length / Track, 3);

        var (fitStart, fitLength) = ScrollList.Thumb(Track, View * 0.5f, View, 0f, MinThumb);
        Assert.Equal(0f, fitStart);
        Assert.Equal(Track, fitLength);

        var huge = View * 1000f;
        var (_, tinyLength) = ScrollList.Thumb(Track, huge, View, 0f, MinThumb);
        Assert.Equal(MinThumb, tinyLength);
    }

    [Fact]
    public void ThumbSpansTheTrackAcrossTheScrollRange()
    {
        var content = View * 5f;
        var max = ScrollList.MaxOffset(content, View);

        Assert.Equal(0f, ScrollList.Thumb(Track, content, View, 0f, MinThumb).Start);
        var end = ScrollList.Thumb(Track, content, View, max, MinThumb);
        Assert.True(MathF.Abs(end.Start + end.Length - Track) < Eps);

        var previous = -1f;
        for (var i = 0; i <= 20; i++)
        {
            var start = ScrollList.Thumb(Track, content, View, max * i / 20f, MinThumb).Start;
            Assert.True(start >= previous, $"thumb start went backwards at step {i}");
            previous = start;
        }
    }

    [Fact]
    public void ThumbStartInvertsToOffset()
    {
        foreach (var content in new[] { View * 1.5f, View * 5f, View * 1000f })
        {
            var max = ScrollList.MaxOffset(content, View);
            foreach (var fraction in new[] { 0f, 0.1f, 0.5f, 0.77f, 1f })
            {
                var offset = max * fraction;
                var start = ScrollList.Thumb(Track, content, View, offset, MinThumb).Start;
                var back = ScrollList.OffsetForThumbStart(start, Track, content, View, MinThumb);
                Assert.True(MathF.Abs(back - offset) < max * 1e-4f + Eps,
                    $"content {content}, offset {offset} came back as {back}");
            }
        }
    }

    [Fact]
    public void RowAtAfterScrollAgreesWithRowTop()
    {
        const int rowCount = 30;
        var offset = RowHeight * 2.5f;

        var (first, end) = ScrollList.VisibleRange(offset, RowHeight, rowCount, View);
        for (var r = first; r < end; r++)
        {
            var mid = ScrollList.RowTop(r, offset, RowHeight) + RowHeight / 2f;
            if (mid < 0f || mid >= View) continue;
            Assert.Equal(r, ScrollList.RowAt(mid, offset, RowHeight, rowCount, View));
        }

        // Rows scrolled wholly out above the viewport are never hit from inside it.
        for (var y = 0f; y < View; y += 1f)
        {
            var hit = ScrollList.RowAt(y, offset, RowHeight, rowCount, View);
            Assert.NotNull(hit);
            Assert.True(ScrollList.RowTop(hit!.Value, offset, RowHeight) >= -RowHeight,
                $"y {y} hit row {hit} which lies above the viewport");
        }

        Assert.Null(ScrollList.RowAt(-1f, offset, RowHeight, rowCount, View));
        Assert.Null(ScrollList.RowAt(View, offset, RowHeight, rowCount, View));

        // A short list: a point below its last row indexes past rowCount.
        const int shortCount = 3;
        Assert.Null(ScrollList.RowAt(RowHeight * shortCount + 1f, 0f, RowHeight, shortCount, View));
    }

    [Theory]
    [InlineData(0f, 30)]
    [InlineData(25f, 30)]
    [InlineData(100f, 30)]
    [InlineData(900f, 30)]
    [InlineData(0f, 3)]
    [InlineData(0f, 0)]
    public void VisibleRangeCoversTheViewport(float offset, int rowCount)
    {
        var clamped = ScrollList.Clamp(offset, rowCount * RowHeight, View);
        var (first, end) = ScrollList.VisibleRange(clamped, RowHeight, rowCount, View);

        Assert.InRange(first, 0, rowCount);
        Assert.InRange(end, first, rowCount);
        if (rowCount == 0)
        {
            Assert.Equal(first, end);
            return;
        }

        var firstTop = ScrollList.RowTop(first, clamped, RowHeight);
        Assert.True(firstTop <= 0f && 0f < firstTop + RowHeight, $"first row top {firstTop} does not straddle the viewport top");
        Assert.True(end == rowCount || ScrollList.RowTop(end, clamped, RowHeight) >= View,
            $"range ends at {end} before the viewport bottom");
    }
}
