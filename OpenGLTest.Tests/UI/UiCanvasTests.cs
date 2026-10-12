using System;
using System.Numerics;
using VoxelEngine.UI;
using Xunit;

namespace VoxelEngine.Tests.UI;

public sealed class UiCanvasTests
{
    private const float Eps = 1e-3f;
    private static Vector2 Ref => UiCanvas.ReferenceSize;

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1366, 768)]
    [InlineData(1920, 1200)]
    [InlineData(3440, 1440)]
    [InlineData(1080, 1920)]
    public void CanvasIsNeverSmallerThanReferenceAndTouchesItOnOneAxis(int w, int h)
    {
        var window = new Vector2(w, h);
        var canvas = UiCanvas.CanvasSizeFor(window, UiCanvas.ScaleFor(window, Ref));

        Assert.True(canvas.X >= Ref.X - Eps, $"canvas width {canvas.X} below reference");
        Assert.True(canvas.Y >= Ref.Y - Eps, $"canvas height {canvas.Y} below reference");
        Assert.True(MathF.Abs(canvas.X - Ref.X) < Eps || MathF.Abs(canvas.Y - Ref.Y) < Eps,
            $"canvas {canvas} touches the reference on neither axis");
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1366, 768)]
    [InlineData(3440, 1440)]
    [InlineData(1080, 1920)]
    public void CanvasKeepsTheWindowAspect(int w, int h)
    {
        var window = new Vector2(w, h);
        var canvas = UiCanvas.CanvasSizeFor(window, UiCanvas.ScaleFor(window, Ref));
        Assert.Equal(window.X / window.Y, canvas.X / canvas.Y, 3);
    }

    [Fact]
    public void ReferenceSizedWindowIsIdentity()
    {
        Assert.Equal(1f, UiCanvas.ScaleFor(Ref, Ref));
        var p = new Vector2(123.5f, 456.25f);
        Assert.Equal(p, UiCanvas.WindowToCanvas(p, 1f));
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(2560, 1440)]
    [InlineData(1366, 768)]
    public void WindowToCanvasRoundTrips(int w, int h)
    {
        var window = new Vector2(w, h);
        float s = UiCanvas.ScaleFor(window, Ref);
        var p = new Vector2(w * 0.3f, h * 0.7f);
        var back = UiCanvas.CanvasToWindow(UiCanvas.WindowToCanvas(p, s), s);
        Assert.Equal(p.X, back.X, 2);
        Assert.Equal(p.Y, back.Y, 2);

        var centre = UiCanvas.WindowToCanvas(window / 2f, s);
        var canvasCentre = UiCanvas.CanvasSizeFor(window, s) / 2f;
        Assert.Equal(canvasCentre.X, centre.X, 2);
        Assert.Equal(canvasCentre.Y, centre.Y, 2);
    }

    [Theory]
    [InlineData(1280, 720, 1)]
    [InlineData(1280, 720, 2)]
    [InlineData(1366, 768, 1)]
    [InlineData(1366, 768, 2)]
    public void ScissorOfWholeCanvasIsWholeFramebuffer(int w, int h, int dpi)
    {
        var window = new Vector2(w, h);
        var fb = window * dpi;
        float s = UiCanvas.ScaleFor(window, Ref);
        var canvas = UiCanvas.CanvasSizeFor(window, s);

        var r = UiCanvas.ScissorRect(Vector2.Zero, canvas, s, window, fb);
        Assert.Equal((0, 0, (int)fb.X, (int)fb.Y), r);
    }

    [Fact]
    public void ScissorFlipsY()
    {
        var window = new Vector2(1280, 720);
        float s = UiCanvas.ScaleFor(window, Ref);
        var canvas = UiCanvas.CanvasSizeFor(window, s);

        var top = UiCanvas.ScissorRect(new Vector2(10, 0), new Vector2(200, 100), s, window, window);
        Assert.Equal((int)window.Y, top.Y + top.Height);

        var bottom = UiCanvas.ScissorRect(new Vector2(10, canvas.Y - 100), new Vector2(200, 100), s, window, window);
        Assert.Equal(0, bottom.Y);
    }

    [Fact]
    public void ScissorDoublesWithAHiDpiFramebufferAtReferenceScale()
    {
        var window = Ref;
        var tl = new Vector2(30, 140);
        var size = new Vector2(400, 250);

        var one = UiCanvas.ScissorRect(tl, size, 1f, window, window);
        var two = UiCanvas.ScissorRect(tl, size, 1f, window, window * 2f);
        Assert.Equal((one.X * 2, one.Y * 2, one.Width * 2, one.Height * 2), two);
    }

    [Fact]
    public void ScissorAtNonIdentityScaleContainsDoubledRectWithinAPixel()
    {
        var window = new Vector2(1280, 720);
        float s = UiCanvas.ScaleFor(window, Ref);
        var tl = new Vector2(33, 141);
        var size = new Vector2(401, 257);

        var one = UiCanvas.ScissorRect(tl, size, s, window, window);
        var two = UiCanvas.ScissorRect(tl, size, s, window, window * 2f);

        int oneRight = (one.X + one.Width) * 2, oneTop = (one.Y + one.Height) * 2;
        int twoRight = two.X + two.Width, twoTop = two.Y + two.Height;

        // The doubled-framebuffer rect sits inside 2x the single one (outward rounding at 1x can only
        // grow it) and each edge differs by at most one framebuffer pixel.
        Assert.True(two.X >= one.X * 2 && two.X - one.X * 2 <= 1);
        Assert.True(two.Y >= one.Y * 2 && two.Y - one.Y * 2 <= 1);
        Assert.True(twoRight <= oneRight && oneRight - twoRight <= 1);
        Assert.True(twoTop <= oneTop && oneTop - twoTop <= 1);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void EffectiveScaleFollowsTheSettingUntilItStopsFitting(int w, int h)
    {
        const float eps = 1e-5f;
        var window = new Vector2(w, h);
        var fit = UiCanvas.ScaleFor(window, UiCanvas.ReferenceSize);

        Assert.Equal(fit, UiCanvas.EffectiveScale(null, window, UiCanvas.ReferenceSize));
        Assert.Equal(fit, UiCanvas.EffectiveScale(0f, window, UiCanvas.ReferenceSize));
        Assert.Equal(fit, UiCanvas.EffectiveScale(-1f, window, UiCanvas.ReferenceSize));

        foreach (var s in GameSettings.GuiScales)
        {
            var effective = UiCanvas.EffectiveScale(s, window, UiCanvas.ReferenceSize);
            Assert.True(effective <= fit + eps);
            Assert.Equal(s <= fit ? s : fit, effective);
        }
    }

    [Fact]
    public void ScaleLabelNamesTheEffectiveScaleOnlyWhenCapped()
    {
        Assert.Equal("Auto", UiCanvas.ScaleLabel(null, 0.5f));
        Assert.Equal("Auto", UiCanvas.ScaleLabel(null, 2f));

        foreach (var s in GameSettings.GuiScales)
        {
            var fits = UiCanvas.ScaleLabel(s, s);
            Assert.Equal(UiCanvas.FormatScale(s), fits);
            Assert.DoesNotContain("(", fits);

            var effective = s * 0.6f;
            var capped = UiCanvas.ScaleLabel(s, effective);
            Assert.StartsWith(UiCanvas.FormatScale(s), capped);
            Assert.Contains(UiCanvas.FormatScale(effective), capped);
        }
    }
}
