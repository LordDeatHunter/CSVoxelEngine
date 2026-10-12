using System;
using silkgltriangle;
using Xunit;

namespace silkgltriangle.Tests;

public class SpringTests
{
    private static float Run(ref Spring spring, float target, float seconds, float dt)
    {
        for (var t = 0f; t < seconds; t += dt) spring.Step(target, dt);
        return spring.Value;
    }

    [Fact]
    public void ACriticallyDampedSpringConvergesWithoutOvershoot()
    {
        var spring = new Spring(3f, 1f);
        var peak = 0f;
        for (var i = 0; i < 300; i++)
        {
            spring.Step(1f, 1f / 60f);
            peak = MathF.Max(peak, spring.Value);
        }

        Assert.True(peak <= 1.001f);
        Assert.True(spring.IsSettled(1f, 0.01f));
    }

    [Fact]
    public void AnUnderdampedSpringOvershootsThenSettles()
    {
        var spring = new Spring(3f, 0.25f);
        var peak = 0f;
        for (var i = 0; i < 600; i++)
        {
            spring.Step(1f, 1f / 60f);
            peak = MathF.Max(peak, spring.Value);
        }

        Assert.True(peak > 1.1f);
        Assert.True(spring.IsSettled(1f, 0.02f));
    }

    [Fact]
    public void AnImpulseDisplacesARestingSpringAndItReturns()
    {
        var spring = new Spring(4f, 0.4f);
        spring.Impulse(5f);
        spring.Step(0f, 0.05f);
        Assert.True(MathF.Abs(spring.Value) > 0.01f);

        Run(ref spring, 0f, 4f, 1f / 60f);
        Assert.True(spring.IsSettled(0f, 0.01f));
    }

    [Fact]
    public void FrameTimeDoesNotChangeWhereTheSpringEndsUp()
    {
        var fine = new Spring(3f, 0.4f);
        var coarse = new Spring(3f, 0.4f);
        Run(ref fine, 1f, 0.5f, 1f / 240f);
        Run(ref coarse, 1f, 0.5f, 1f / 30f);

        Assert.InRange(MathF.Abs(fine.Value - coarse.Value), 0f, 0.05f);
    }

    [Fact]
    public void OneShotEasesStartAtZeroAndFinishAtOne()
    {
        Assert.Equal(0f, MathUtils.EaseOutBack(0f), 4);
        Assert.Equal(1f, MathUtils.EaseOutBack(1f), 4);
        Assert.Equal(0f, MathUtils.EaseOutElastic(0f), 4);
        Assert.Equal(1f, MathUtils.EaseOutElastic(1f), 4);
        Assert.Equal(0f, MathUtils.EaseOutBounce(0f), 4);
        Assert.Equal(1f, MathUtils.EaseOutBounce(1f), 4);
        Assert.Equal(0f, MathUtils.EaseInOutCubic(0f), 4);
        Assert.Equal(1f, MathUtils.EaseInOutCubic(1f), 4);
        Assert.True(MathUtils.EaseOutBack(0.7f) > 1f);
    }
}
