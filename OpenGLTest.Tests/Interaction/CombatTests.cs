using System;
using System.Numerics;
using VoxelEngine.Interaction;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

public sealed class CombatTests
{
    public CombatTests() => TestContent.EnsureLoaded();

    [Fact]
    public void UndeadBonusRaisesOnlyBlowsAgainstTheUndead()
    {
        Assert.True(Combat.VersusUndead(10, true, 0.1f) > 10);
        Assert.Equal(10, Combat.VersusUndead(10, false, 0.1f));
        Assert.Equal(10, Combat.VersusUndead(10, true, 0f));
        Assert.Equal(0, Combat.VersusUndead(0, true, 0.1f));
        for (var d = 1; d <= 50; d++)
        {
            Assert.True(Combat.VersusUndead(d, true, 0.1f) >= d);
            Assert.True(Combat.VersusUndead(d, true, 0.2f) >= Combat.VersusUndead(d, true, 0.1f));
        }
    }

    [Fact]
    public void PunchConeAcceptsAheadWithinReachAndRejectsBehindAndBeyond()
    {
        var origin = Vector3.Zero;
        var forward = new Vector3(1f, 0f, 0f);

        Assert.True(Combat.InPunchCone(origin, forward, new Vector3(2f, 0f, 0f), out var distance));
        Assert.Equal(2f, distance, precision: 4);
        Assert.False(Combat.InPunchCone(origin, forward, new Vector3(-1f, 0f, 0f), out _));
        Assert.False(Combat.InPunchCone(origin, forward, forward * (Combat.PunchReach + 1f), out _));
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

    [Fact]
    public void ReachEdgeIsInclusive()
    {
        var origin = Vector3.Zero;
        var forward = new Vector3(1f, 0f, 0f);
        Assert.True(Combat.InPunchCone(origin, forward, forward * Combat.PunchReach, out var distance));
        Assert.Equal(Combat.PunchReach, distance, precision: 4);
        Assert.False(Combat.InPunchCone(origin, forward, forward * (Combat.PunchReach + 0.1f), out _));
        Assert.True(Combat.InFrontHemisphere(origin, forward, forward * Combat.PunchReach, out _));
        Assert.False(Combat.InFrontHemisphere(origin, forward, forward * (Combat.PunchReach + 0.1f), out _));
    }

    [Fact]
    public void SliceHitsAFrontHemisphereWithinPunchReach()
    {
        var origin = Vector3.Zero;
        var forward = new Vector3(1f, 0f, 0f);

        Assert.True(Combat.InFrontHemisphere(origin, forward, new Vector3(2f, 0f, 0f), out var distance));
        Assert.Equal(2f, distance, precision: 4);
        Assert.True(Combat.InFrontHemisphere(origin, forward, new Vector3(0.1f, 0f, 2f), out _));
        Assert.False(Combat.InFrontHemisphere(origin, forward, new Vector3(-1f, 0f, 0f), out _));
        Assert.False(Combat.InFrontHemisphere(origin, forward, forward * (Combat.PunchReach + 1f), out _));
        Assert.False(Combat.InFrontHemisphere(origin, forward, new Vector3(0f, 0f, 2f), out _));
        Assert.False(Combat.InFrontHemisphere(origin, forward, origin, out _));
    }

    [Fact]
    public void SliceReturnSwingHalvesForwardPowerFlooredAtOne()
    {
        var blade = ContentRegistry.RequireItem("obsidian_blade");
        var forward = Combat.SliceDamage(blade, returnSwing: false);
        Assert.Equal((int)MathF.Round(forward / 2f, MidpointRounding.AwayFromZero), Combat.SliceDamage(blade, returnSwing: true));
        Assert.True(forward > Combat.SliceDamage(null, returnSwing: false));

        Assert.Equal(1, Combat.SliceDamage(null, returnSwing: false));
        Assert.Equal(1, Combat.SliceDamage(null, returnSwing: true));
    }

    [Fact]
    public void TheSwordChainRewardsTheChopAndDiscountsTheQuickBackCut()
    {
        var sword = ContentRegistry.RequireItem("iron_sword");
        var cut = Combat.SwordDamage(sword, SwordSwing.Cut);
        var backCut = Combat.SwordDamage(sword, SwordSwing.BackCut);
        var chop = Combat.SwordDamage(sword, SwordSwing.Chop);

        Assert.True(chop > cut, $"the chop ({chop}) should out-hit the cut ({cut})");
        Assert.True(cut > backCut, $"the cut ({cut}) should out-hit the back cut ({backCut})");
        Assert.True(backCut >= 1);
        // The cut is the item's own entityDamage, as a punch with it would be
        Assert.Equal(Combat.PunchDamage(sword), cut);
        // Bare-handed, every swing still lands at least a point
        foreach (var swing in SwordSwings.All)
            Assert.True(Combat.SwordDamage(null, swing) >= 1);
    }

    [Fact]
    public void TheChopIsANarrowSingleTargetBlowAndTheCutsSweepSeveral()
    {
        Assert.True(SwordSwings.IsCone(SwordSwing.Chop));
        Assert.Equal(1, SwordSwings.MaxTargets(SwordSwing.Chop));
        foreach (var sweep in new[] { SwordSwing.Cut, SwordSwing.BackCut })
        {
            Assert.False(SwordSwings.IsCone(sweep));
            Assert.True(SwordSwings.MaxTargets(sweep) > 1);
            Assert.True(SwordSwings.ShoveFactor(SwordSwing.Chop) > SwordSwings.ShoveFactor(sweep));
            Assert.True(SwordSwings.HitStopWeight(SwordSwing.Chop) > SwordSwings.HitStopWeight(sweep));
        }
    }
}
