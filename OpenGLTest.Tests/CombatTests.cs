using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class CombatTests
{
    [Fact]
    public void PunchConeAcceptsAheadWithinReachAndRejectsBehindAndBeyond()
    {
        var origin = Vector3.Zero;
        var forward = new Vector3(1f, 0f, 0f);

        Assert.True(Combat.InPunchCone(origin, forward, new Vector3(2f, 0f, 0f), out var distance));
        Assert.Equal(2f, distance, precision: 4);
        Assert.False(Combat.InPunchCone(origin, forward, new Vector3(-1f, 0f, 0f), out _));
        Assert.False(Combat.InPunchCone(origin, forward, new Vector3(4f, 0f, 0f), out _));
        Assert.False(Combat.InPunchCone(origin, forward, origin, out _));
    }

    [Fact]
    public void PunchConeUsesTheHalfAngle()
    {
        var origin = Vector3.Zero;
        var forward = new Vector3(1f, 0f, 0f);
        var inside = new Vector3(MathF.Cos(0.4f), MathF.Sin(0.4f), 0f) * 2f;  // ~23 degrees off aim
        var outside = new Vector3(MathF.Cos(0.7f), MathF.Sin(0.7f), 0f) * 2f; // ~40 degrees off aim

        Assert.True(Combat.InPunchCone(origin, forward, inside, out _));
        Assert.False(Combat.InPunchCone(origin, forward, outside, out _));
    }

    [Fact]
    public void PunchConeMeasuresFromTheAimDirection()
    {
        var origin = new Vector3(10f, 20f, 30f);
        var forward = -Vector3.UnitZ;
        var target = origin + new Vector3(0.5f, 0.5f, -2f);

        Assert.True(Combat.InPunchCone(origin, forward, target, out var distance));
        Assert.Equal(2.1213f, distance, precision: 3);
    }
}
