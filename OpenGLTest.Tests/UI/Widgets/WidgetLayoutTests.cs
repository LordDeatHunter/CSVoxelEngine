using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.UI;
using VoxelEngine.UI.Widgets;
using Xunit;

namespace VoxelEngine.Tests.UI.Widgets;

public sealed class WidgetLayoutTests
{
    private static float Mono(ReadOnlySpan<char> text) => text.Length * 10f;

    private static readonly Vector2 Canvas = new(1280f, 720f);

    [Fact]
    public void TwoRectanglesSharingAnEdgeNeverBothClaimAPointOnIt()
    {
        var a = UiRect.FromTopLeft(new Vector2(0f, 0f), new Vector2(50f, 20f));
        var b = UiRect.FromTopLeft(new Vector2(50f, 0f), new Vector2(50f, 20f));
        var edge = new Vector2(50f, 10f);
        Assert.False(a.Contains(edge));
        Assert.True(b.Contains(edge));
    }

    [Fact]
    public void ATabHitTestMatchesTheDrawnRectanglesAndTheGapsBelongToNoTab()
    {
        var strip = new TabStripLayout(UiRect.FromTopLeft(new Vector2(40f, 80f), new Vector2(600f, 60f)), 4, 4f);
        for (var i = 0; i < strip.Count; i++)
        {
            Assert.Equal(i, strip.TabAt(strip.Tab(i).Center));
            Assert.Equal(i, strip.TabAt(new Vector2(strip.Tab(i).Left + 0.01f, strip.Tab(i).Center.Y)));
            if (i < strip.Count - 1)
            {
                var gap = new Vector2(strip.Tab(i).Right + strip.Gap * 0.5f, strip.Tab(i).Center.Y);
                Assert.Equal(-1, strip.TabAt(gap));
            }
        }

        Assert.Equal(-1, strip.TabAt(new Vector2(strip.Strip.Right + 1f, strip.Strip.Center.Y)));
    }

    [Fact]
    public void TabsFillTheStripExactly()
    {
        var strip = new TabStripLayout(UiRect.FromTopLeft(new Vector2(0f, 0f), new Vector2(500f, 40f)), 3, 6f);
        Assert.Equal(strip.Strip.Left, strip.Tab(0).Left, 3);
        Assert.Equal(strip.Strip.Right, strip.Tab(2).Right, 3);
    }

    [Fact]
    public void TheUnderlineSlidesBetweenTabsWithoutJumping()
    {
        var strip = new TabStripLayout(UiRect.FromTopLeft(new Vector2(0f, 0f), new Vector2(500f, 40f)), 4, 4f);
        Assert.Equal(strip.Tab(1).Left, strip.Underline(1f).Left, 3);
        var previous = strip.Underline(0f).Left;
        for (var p = 0.1f; p <= 3f; p += 0.1f)
        {
            var left = strip.Underline(p).Left;
            Assert.True(left >= previous - 0.001f);
            previous = left;
        }
    }

    [Fact]
    public void AButtonStateFollowsOneRuleAndDisabledIgnoresThePointer()
    {
        Assert.Equal(WidgetState.Disabled, ButtonLayout.StateOf(false, true, true));
        Assert.Equal(WidgetState.Pressed, ButtonLayout.StateOf(true, true, true));
        Assert.Equal(WidgetState.Hover, ButtonLayout.StateOf(true, true, false));
        Assert.Equal(WidgetState.Normal, ButtonLayout.StateOf(true, false, true));
        var states = new[]
        {
            WidgetState.Disabled, WidgetState.Pressed, WidgetState.Hover, WidgetState.Normal,
        };
        Assert.Equal(states.Length, states.Distinct().Count());
    }

    [Fact]
    public void AButtonOnlyHitsInsideItsRectangleWhileEnabled()
    {
        var button = new ButtonLayout(UiRect.FromTopLeft(new Vector2(100f, 100f), new Vector2(160f, 48f)));
        Assert.True(button.Hit(button.Rect.Center, true));
        Assert.False(button.Hit(button.Rect.Center, false));
        Assert.False(button.Hit(button.Rect.Center + new Vector2(200f, 0f), true));
        Assert.Equal(WidgetState.Hover, button.StateAt(button.Rect.Center, false, true));
        Assert.Equal(WidgetState.Normal, button.StateAt(null, false, true));
    }

    [Fact]
    public void TheTextBoxBudgetIsNeverNegativeAndShrinksWithTheWidth()
    {
        var previous = float.MaxValue;
        foreach (var width in new[] { 600f, 300f, 120f, 40f, 4f, 0f })
        {
            var box = new TextBoxLayout(UiRect.FromTopLeft(new Vector2(10f, 10f), new Vector2(width, 36f)));
            Assert.True(box.TextBudget >= 0f);
            Assert.True(box.TextBudget <= previous);
            previous = box.TextBudget;
        }
    }

    [Fact]
    public void ATextBoxCaretSitsAfterTheTextAndInsideTheBoxWhenTheTextFits()
    {
        var box = new TextBoxLayout(UiRect.FromTopLeft(new Vector2(10f, 10f), new Vector2(300f, 36f)));
        var caret = box.Caret(box.TextBudget);
        Assert.True(caret.Right <= box.Box.Right);
        Assert.True(box.Caret(10f).Left > box.Caret(0f).Left);
    }

    [Fact]
    public void AListRowNameNeverOverlapsTheCountOrGoesNegative()
    {
        foreach (var width in new[] { 400f, 200f, 80f, 10f })
        foreach (var count in new[] { 0f, 20f, 90f })
        {
            var row = new ListRowLayout(UiRect.FromTopLeft(new Vector2(40f, 0f), new Vector2(width, 42f)), 28f);
            var name = row.NameWidth(count);
            Assert.True(name >= 0f);
            if (count > 0f && name > 0f)
                Assert.True(row.NameLeft + name <= row.CountLeft(count));
        }
    }

    [Fact]
    public void TheFrameContentIsInsideTheBorderAndBelowTheAccentBar()
    {
        var frame = PanelFrameLayout.Standard(UiRect.FromTopLeft(new Vector2(40f, 80f), new Vector2(400f, 300f)));
        Assert.True(frame.Content.Left > frame.Outer.Left + frame.BorderWidth);
        Assert.True(frame.Content.Right < frame.Outer.Right - frame.BorderWidth);
        Assert.True(frame.Content.Top >= frame.AccentBar.Bottom);
        Assert.True(frame.Content.Bottom < frame.Outer.Bottom - frame.BorderWidth);
        Assert.True(frame.AccentBar.Left >= frame.Outer.Left && frame.AccentBar.Right <= frame.Outer.Right);
    }

    [Fact]
    public void AFrameTooSmallForItsPaddingHasEmptyContentNotNegativeContent()
    {
        var frame = PanelFrameLayout.Standard(UiRect.FromTopLeft(Vector2.Zero, new Vector2(10f, 10f)));
        Assert.True(frame.Content.Size.X >= 0f && frame.Content.Size.Y >= 0f);
    }

    [Fact]
    public void ScrollViewHitTestsAndRowRectsAgree()
    {
        var view = new ScrollViewLayout(UiRect.FromTopLeft(new Vector2(40f, 100f), new Vector2(300f, 200f)), 40f, 20);
        foreach (var offset in new[] { 0f, 13f, 120f, view.Clamp(1e6f) })
        {
            var (first, end) = view.VisibleRows(offset);
            for (var row = first; row < end; row++)
            {
                var rect = view.RowRect(row, offset);
                var probe = new Vector2(rect.Center.X, Math.Clamp(rect.Center.Y, view.View.Top, view.View.Bottom - 0.01f));
                if (probe.Y >= rect.Top && probe.Y < rect.Bottom)
                    Assert.Equal(row, view.RowAt(probe, offset));
            }
        }

        Assert.Null(view.RowAt(new Vector2(view.View.Right + 5f, view.View.Center.Y), 0f));
    }

    [Fact]
    public void RevealingARowKeepsItWhollyInViewAndMovesAsLittleAsPossible()
    {
        var view = new ScrollViewLayout(UiRect.FromTopLeft(new Vector2(0f, 0f), new Vector2(300f, 200f)), 40f, 30);
        for (var row = 0; row < view.RowCount; row++)
            foreach (var offset in new[] { 0f, 55f, 400f, 1000f })
            {
                var revealed = view.OffsetToReveal(row, view.Clamp(offset));
                var rect = view.RowRect(row, revealed);
                Assert.True(rect.Top >= view.View.Top - 0.01f && rect.Bottom <= view.View.Bottom + 0.01f);
                var start = view.Clamp(offset);
                if (view.RowRect(row, start).Top >= view.View.Top && view.RowRect(row, start).Bottom <= view.View.Bottom)
                    Assert.Equal(start, revealed, 3);
            }
    }

    [Fact]
    public void ATooltipStaysInsideTheCanvasWhereverTheAnchorIs()
    {
        var lines = new[] { "Crucible", "Holds 800 ml of molten metal that keeps cooling while carried around", "Teaches: fire" };
        foreach (var anchor in new[]
                 {
                     new Vector2(0f, 0f), new Vector2(640f, 360f), new Vector2(1270f, 710f), new Vector2(1280f, 0f),
                 })
        {
            var tip = TooltipLayout.Compute(lines, anchor, Canvas, 300f, 6, 20f, Mono);
            Assert.True(tip.Box.Left >= 0f && tip.Box.Top >= 0f);
            Assert.True(tip.Box.Right <= Canvas.X + 0.01f && tip.Box.Bottom <= Canvas.Y + 0.01f);
        }
    }

    [Fact]
    public void ATooltipWrapsToItsMaxWidthAndGrowsTallerWithMoreText()
    {
        var short1 = TooltipLayout.Compute(["Stone"], new Vector2(100f, 100f), Canvas, 200f, 8, 20f, Mono);
        var long1 = TooltipLayout.Compute([new string('a', 5) + " " + string.Join(' ', Enumerable.Repeat("word", 40))],
            new Vector2(100f, 100f), Canvas, 200f, 50, 20f, Mono);
        Assert.True(long1.Lines.Count > short1.Lines.Count);
        Assert.True(long1.Box.Size.Y > short1.Box.Size.Y);
        Assert.All(long1.Lines, l => Assert.True(Mono(l) <= 200f));
    }

    [Fact]
    public void ATooltipIsClampedToItsLineCountAndSaysSo()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 200));
        var tip = TooltipLayout.Compute([text], new Vector2(10f, 10f), Canvas, 200f, 4, 20f, Mono);
        Assert.Equal(4, tip.Lines.Count);
        Assert.True(tip.Clamped);
        Assert.EndsWith(TextFit.Ellipsis, tip.Lines[^1]);
        Assert.True(Mono(tip.Lines[^1]) <= 200f);
    }

    [Fact]
    public void AShortTooltipIsNotMarkedClamped()
    {
        var tip = TooltipLayout.Compute(["Stone", "Block"], new Vector2(10f, 10f), Canvas, 200f, 4, 20f, Mono);
        Assert.False(tip.Clamped);
        Assert.Equal(2, tip.Lines.Count);
    }
}
