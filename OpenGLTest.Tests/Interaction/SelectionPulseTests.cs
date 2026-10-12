using System;
using VoxelEngine.Interaction;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// The selection outline's breathing (<see cref="SelectionPulse"/>, the twin of
/// selection-fragment.glsl's <c>uPulseDepth</c> term): it swings between the configured floor and
/// full opacity while the player looks at a block, and holds steady while the block is mined.
/// </summary>
public sealed class SelectionPulseTests
{
    private static readonly float Period = WorldGenerationConfig.SelectionPulsePeriodSeconds;

    [Fact]
    public void TheOutlineBreathesBetweenItsFloorAndFullOpacityWithinOneBreath()
    {
        var depth = SelectionPulse.Depth(mining: false);
        var floor = 1f - depth;
        float lowest = 1f, highest = 0f;
        for (var t = 0f; t <= Period; t += Period / 400f)
        {
            var o = SelectionPulse.Opacity(t, depth);
            Assert.InRange(o, floor - 1e-5f, 1f + 1e-5f);
            lowest = MathF.Min(lowest, o);
            highest = MathF.Max(highest, o);
        }

        Assert.Equal(1f, highest, 3);
        Assert.Equal(floor, lowest, 3);
        Assert.Equal(WorldGenerationConfig.SelectionPulseMinOpacity, floor, 5);
    }

    [Fact]
    public void TheBreathRepeatsEveryPeriod()
    {
        var depth = SelectionPulse.Depth(mining: false);
        for (var t = 0f; t < 3f * Period; t += 0.07f)
            Assert.Equal(SelectionPulse.Opacity(t, depth), SelectionPulse.Opacity(t + Period, depth), 3);
    }

    [Fact]
    public void MiningHoldsTheOutlineSteadyAtFullOpacity()
    {
        var depth = SelectionPulse.Depth(mining: true);
        Assert.Equal(0f, depth);
        for (var t = 0f; t < 2f * Period; t += 0.05f)
            Assert.Equal(1f, SelectionPulse.Opacity(t, depth));
    }

    [Fact]
    public void ADeeperBreathNeverReadsBrighter()
    {
        for (var t = 0f; t < Period; t += 0.05f)
        {
            var previous = SelectionPulse.Opacity(t, 0f);
            for (var depth = 0f; depth <= 1f; depth += 0.05f)
            {
                var o = SelectionPulse.Opacity(t, depth);
                Assert.True(o <= previous + 1e-6f, $"opacity rose from {previous} to {o} at depth {depth}");
                previous = o;
            }
        }
    }
}
