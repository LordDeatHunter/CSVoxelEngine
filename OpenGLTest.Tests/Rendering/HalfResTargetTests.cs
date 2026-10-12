using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <c>HalfResTarget</c> is the GL-free arithmetic behind the half-resolution underwater-rays render
/// target: what size it wants for a given viewport, and whether a resize has to tear down and
/// re-create the GL objects. The GL half of that target — the RGBA16F texture allocation and the
/// framebuffer-completeness check — is deliberately NOT covered here, exactly as
/// ReflectionReuseTests.cs:57 says of the reflection target: it needs a live GL context and is only
/// reachable by running the game. What is pinned here is the sizing and the recreate decision, which
/// is where a resize bug would leave a stale-size or 0-sized target.
///
/// The minimum is expressed against <c>HalfResTarget.MinDimension</c> rather than a bare literal so
/// retuning the floor cannot silently turn a test into a tautology.
/// </summary>
public sealed class HalfResTargetTests
{
    [Fact]
    public void HalvesAnEvenViewport()
    {
        var size = HalfResTarget.For(1920, 1080);

        Assert.Equal(960, size.Width);
        Assert.Equal(540, size.Height);
    }

    [Fact]
    public void RoundsOddDimensionsUp()
    {
        // Ceil is what guarantees the half-res grid still covers the last column and row: 1281 / 2
        // is 640.5, and a target 640 wide would leave the rightmost full-res column unmarched.
        var size = HalfResTarget.For(1281, 721);

        Assert.Equal(641, size.Width);
        Assert.Equal(361, size.Height);
    }

    [Fact]
    public void ClampsDegenerateViewportsToTheMinimum()
    {
        // The minimize case: a window can report a 0- or 1-pixel (or, defensively, negative)
        // viewport, and GL must never be asked for a 0-sized texture.
        foreach (var (w, h) in new[] { (1, 1), (0, 0), (-4, -100) })
        {
            var size = HalfResTarget.For(w, h);

            Assert.Equal(HalfResTarget.MinDimension, size.Width);
            Assert.Equal(HalfResTarget.MinDimension, size.Height);
        }
    }

    [Fact]
    public void UnallocatedTargetAlwaysRecreates()
    {
        // Sizes match, but there are no GL objects yet.
        var desired = HalfResTarget.For(1280, 720);

        Assert.True(HalfResTarget.ShouldRecreate(allocated: false, desired, desired));
    }

    [Fact]
    public void MatchingSizeDoesNotRecreate()
    {
        var desired = HalfResTarget.For(1280, 720);

        Assert.False(HalfResTarget.ShouldRecreate(allocated: true, desired, desired));
    }

    [Fact]
    public void EitherAxisChangingRecreates()
    {
        var current = HalfResTarget.For(1280, 720);

        Assert.True(HalfResTarget.ShouldRecreate(allocated: true, current, HalfResTarget.For(1600, 720)));
        Assert.True(HalfResTarget.ShouldRecreate(allocated: true, current, HalfResTarget.For(1280, 800)));
    }

    [Fact]
    public void AdjacentViewportWidthsCanShareAHalfResSize()
    {
        // The drag-resize no-churn property: 1281 and 1282 both halve to 641, so dragging across
        // that pixel must not delete and re-allocate the texture and framebuffer.
        var current = HalfResTarget.For(1281, 720);
        var desired = HalfResTarget.For(1282, 720);

        Assert.Equal(desired, current);
        Assert.False(HalfResTarget.ShouldRecreate(allocated: true, current, desired));
    }
}
