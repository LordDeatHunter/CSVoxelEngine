using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The lava glow pulse (<see cref="LavaPulse"/>, the twin of the term in test-fragment.glsl): a
/// bounded, smooth and slow breathing of the lava's brightness that leaves it alone at zero strength.
/// </summary>
public sealed class LavaPulseTests
{
    private static readonly float Strength = WorldGenerationConfig.LavaPulseStrength;

    [Fact]
    public void TheBrightnessBreathesWithinItsStrengthAroundTheGlow()
    {
        float lowest = float.MaxValue, highest = float.MinValue;
        for (var x = -60f; x <= 60f; x += 1.7f)
        for (var z = -60f; z <= 60f; z += 2.3f)
        for (var t = 0f; t <= 30f; t += 7.5f)
        {
            var field = LavaPulse.Field(new Vector2(x, z), t);
            Assert.InRange(field, 0f, 1f);
            var b = LavaPulse.Brightness(new Vector2(x, z), t, Strength);
            Assert.InRange(b, 1f - Strength - 1e-5f, 1f + Strength + 1e-5f);
            lowest = MathF.Min(lowest, b);
            highest = MathF.Max(highest, b);
        }

        // It really breathes: across a pool and over time it reads both brighter and dimmer.
        Assert.True(lowest < 1f && highest > 1f, $"brightness stayed within {lowest}..{highest}");
    }

    [Fact]
    public void ZeroStrengthLeavesTheGlowAlone()
    {
        for (var x = -20f; x <= 20f; x += 3.1f)
        for (var t = 0f; t <= 20f; t += 4.4f)
            Assert.Equal(1f, LavaPulse.Brightness(new Vector2(x, -x), t, 0f));
    }

    [Fact]
    public void TheFieldIsSmoothAcrossTheSurface()
    {
        // No seams or speckle: a hundredth of a block moves the field by a small, bounded amount.
        const float step = 0.01f;
        for (var x = -30f; x <= 30f; x += 0.61f)
        for (var z = -30f; z <= 30f; z += 1.13f)
        {
            var p = new Vector2(x, z);
            Assert.True(MathF.Abs(LavaPulse.Field(p + new Vector2(step, 0f), 5f) - LavaPulse.Field(p, 5f)) < 0.02f);
            Assert.True(MathF.Abs(LavaPulse.Field(p + new Vector2(0f, step), 5f) - LavaPulse.Field(p, 5f)) < 0.02f);
        }
    }

    [Fact]
    public void TheFieldChangesSlowly()
    {
        // A slow breath: a tenth of a second never swings the field by more than a few percent.
        for (var x = -30f; x <= 30f; x += 1.9f)
        for (var t = 0f; t <= 60f; t += 3.7f)
        {
            var p = new Vector2(x, 0.5f * x);
            Assert.True(MathF.Abs(LavaPulse.Field(p, t + 0.1f) - LavaPulse.Field(p, t)) < 0.05f);
        }
    }
}
