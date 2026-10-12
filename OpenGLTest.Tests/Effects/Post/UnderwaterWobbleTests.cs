using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The underwater screen wobble (<see cref="UnderwaterWobble"/>, the twin of post-fragment.glsl's):
/// nothing in dry air, a gentle bounded sway under water, and a clock that wraps without a jump.
/// </summary>
public sealed class UnderwaterWobbleTests
{
    private const float Height = 1080f;

    [Fact]
    public void DryAirLeavesEveryPixelWhereItIs()
    {
        for (var u = 0f; u <= 1f; u += 0.1f)
        for (var t = 0f; t < 20f; t += 1.3f)
            Assert.Equal(Vector2.Zero, UnderwaterWobble.Offset(new Vector2(u, 1f - u), t, 0f, Height));
    }

    [Fact]
    public void TheSwayIsGentleAndGrowsWithTheUnderwaterAmount()
    {
        var cap = WorldGenerationConfig.UnderwaterWobbleScreenShare * Height + 1e-3f;
        var largest = 0f;
        for (var u = 0f; u <= 1f; u += 0.05f)
        for (var t = 0f; t < 30f; t += 0.37f)
        {
            var full = UnderwaterWobble.Offset(new Vector2(u, u * 0.7f), t, 1f, Height);
            var half = UnderwaterWobble.Offset(new Vector2(u, u * 0.7f), t, 0.5f, Height);
            Assert.InRange(MathF.Abs(full.X), 0f, cap);
            Assert.InRange(MathF.Abs(full.Y), 0f, cap);
            Assert.True(half.Length() <= full.Length() + 1e-4f);
            largest = MathF.Max(largest, full.Length());
        }

        Assert.True(largest > 0.5f * cap, "the wobble never visibly moved a pixel");
    }

    [Fact]
    public void TheClockWrapsWithoutAJump()
    {
        var uv = new Vector2(0.3f, 0.8f);
        var period = (float)UnderwaterWobble.Period;
        var atStart = UnderwaterWobble.Offset(uv, 0f, 1f, Height);
        var atEnd = UnderwaterWobble.Offset(uv, period, 1f, Height);
        Assert.True(Vector2.Distance(atStart, atEnd) < 0.01f, "the sway jumped where the clock wraps");

        // A step can move the sway at most amplitude * speed * dt along each axis
        const double step = 0.05;
        var amplitude = WorldGenerationConfig.UnderwaterWobbleScreenShare * Height;
        var bound = 1.5f * MathF.Sqrt(2f) * amplitude * WorldGenerationConfig.UnderwaterWobbleSpeed * (float)step;
        var previous = UnderwaterWobble.Offset(uv, UnderwaterWobble.WrapTime(10_000.0), 1f, Height);
        for (var t = 10_000.0; t < 10_000.0 + 2.0 * UnderwaterWobble.Period; t += step)
        {
            var o = UnderwaterWobble.Offset(uv, UnderwaterWobble.WrapTime(t), 1f, Height);
            Assert.True(Vector2.Distance(o, previous) < bound, $"the sway jumped at t = {t}");
            previous = o;
        }
    }
}
