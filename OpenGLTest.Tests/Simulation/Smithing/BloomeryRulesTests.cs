using VoxelEngine.Simulation.Smithing;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Smithing;

/// <summary>The bloomery's heat model as relations: where the band starts, how heat falls and rises with a landing on
/// the bellows, and how progress runs on from the band up (however hot) and back under it.</summary>
public sealed class BloomeryRulesTests
{
    [Fact]
    public void OnlyAColdFireFailsToSmelt()
    {
        Assert.False(BloomeryRules.Smelts(0f));
        Assert.False(BloomeryRules.Smelts(BloomeryConfig.BandMin - 0.01f));
        Assert.True(BloomeryRules.Smelts(BloomeryConfig.BandMin));
        Assert.True(BloomeryRules.Smelts(1f));
    }

    [Fact]
    public void NoFireIsTooHotAMaxedOneSmeltsAsFastAsOneAtTheBand()
    {
        Assert.True(BloomeryRules.ProgressRate(1f) > 0f);
        Assert.Equal(BloomeryRules.ProgressRate(BloomeryConfig.BandMin), BloomeryRules.ProgressRate(1f));
    }

    [Fact]
    public void UnderTheBandProgressRunsBackAndTheColderTheFaster()
    {
        var justUnder = BloomeryRules.ProgressRate(BloomeryConfig.BandMin - 0.01f);
        var colder = BloomeryRules.ProgressRate(BloomeryConfig.BandMin * 0.5f);
        var dead = BloomeryRules.ProgressRate(0f);
        Assert.True(justUnder < 0f);
        Assert.True(colder < justUnder);
        Assert.True(dead < colder);
        // The rate shrinks to nothing at the band's edge, so crossing it never jumps
        Assert.True(justUnder > -0.1f * BloomeryConfig.ColdReverseRate);
    }

    [Fact]
    public void HeatFallsWithoutLandingsButNeverBelowZero()
    {
        Assert.True(BloomeryRules.Decay(0.6f, 1f) < 0.6f);
        Assert.Equal(0f, BloomeryRules.Decay(0.01f, 1000f));
    }

    [Fact]
    public void ALandingAddsHeatButNeverPastOne()
    {
        var landing = BloomeryRules.LandingHeat(1.2f);
        Assert.True(BloomeryRules.Pump(0.3f, landing) > 0.3f);
        Assert.Equal(1f, BloomeryRules.Pump(0.99f, landing));
        Assert.Equal(1f, BloomeryRules.Pump(1f, landing));
    }

    [Fact]
    public void AFartherFallHeatsMoreUpToACapAndASettleHeatsNothing()
    {
        Assert.Equal(0f, BloomeryRules.LandingHeat(0f));
        Assert.Equal(0f, BloomeryRules.LandingHeat(BloomeryConfig.BellowsMinFallBlocks * 0.5f));
        var low = BloomeryRules.LandingHeat(BloomeryConfig.BellowsMinFallBlocks);
        var high = BloomeryRules.LandingHeat(BloomeryConfig.BellowsMinFallBlocks + 2f);
        Assert.True(low > 0f);
        Assert.True(high > low);
        Assert.Equal(BloomeryConfig.BellowsMaxHeat, BloomeryRules.LandingHeat(1000f));
    }

    [Fact]
    public void JumpingOnTheBellowsOncePerCooldownCanKeepAFireAtTheBandForever()
    {
        // The design rule that makes the bellows playable: a jump on the spot (a ground jump's apex is a little over a
        // block) is a landing that counts, and one every cooldown, or even one a second, outpaces the decay
        const float jumpDrop = 1.1f;
        var perJump = BloomeryRules.LandingHeat(jumpDrop);
        Assert.True(perJump > 0f);
        Assert.True(perJump > BloomeryConfig.HeatDecayPerSecond * BloomeryConfig.PumpCooldownSeconds);
        Assert.True(perJump > BloomeryConfig.HeatDecayPerSecond * 1f);
    }
}
