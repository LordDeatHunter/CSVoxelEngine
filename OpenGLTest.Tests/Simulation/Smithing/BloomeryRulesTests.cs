using VoxelEngine.Simulation.Smithing;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Smithing;

/// <summary>The bloomery's heat model as relations: where the band sits, how heat falls and rises, what burns.</summary>
public sealed class BloomeryRulesTests
{
    [Fact]
    public void TheZonesAreOrderedColdBandHotAndTheBandHasWidth()
    {
        Assert.True(BloomeryConfig.BandMin < BloomeryConfig.BandMax);
        var mid = (BloomeryConfig.BandMin + BloomeryConfig.BandMax) * 0.5f;

        Assert.Equal(HeatZone.Cold, BloomeryRules.ZoneOf(0f));
        Assert.Equal(HeatZone.Cold, BloomeryRules.ZoneOf(BloomeryConfig.BandMin - 0.01f));
        Assert.Equal(HeatZone.Band, BloomeryRules.ZoneOf(mid));
        Assert.Equal(HeatZone.Hot, BloomeryRules.ZoneOf(BloomeryConfig.BandMax + 0.01f));
        Assert.Equal(HeatZone.Hot, BloomeryRules.ZoneOf(1f));
    }

    [Fact]
    public void OnlyAColdFireFailsToSmelt()
    {
        Assert.False(BloomeryRules.Smelts(0f));
        Assert.True(BloomeryRules.Smelts(BloomeryConfig.BandMin));
        Assert.True(BloomeryRules.Smelts(1f));
    }

    [Fact]
    public void HeatFallsWithoutPumpsButNeverBelowZero()
    {
        Assert.True(BloomeryRules.Decay(0.6f, 1f) < 0.6f);
        Assert.Equal(0f, BloomeryRules.Decay(0.01f, 1000f));
    }

    [Fact]
    public void APumpRaisesHeatButNeverPastOne()
    {
        Assert.True(BloomeryRules.Pump(0.3f) > 0.3f);
        Assert.Equal(1f, BloomeryRules.Pump(0.99f));
        Assert.Equal(1f, BloomeryRules.Pump(1f));
    }

    [Fact]
    public void OneJumpPerPumpCooldownCanKeepAFireInTheBandForever()
    {
        // The design rule that makes the bellows playable: a pump every cooldown outpaces the decay in that time,
        // and one jump every couple of seconds is enough to hold the fire inside the band
        Assert.True(BloomeryConfig.PumpHeat > BloomeryConfig.HeatDecayPerSecond * BloomeryConfig.PumpCooldownSeconds);
        Assert.True(BloomeryConfig.PumpHeat > BloomeryConfig.HeatDecayPerSecond * 2f);
    }

    [Fact]
    public void AnOverheatedFireBurnsItsFuelFasterAndABurntUnitNeedsRealOverheating()
    {
        Assert.True(BloomeryRules.FuelBurnFactor(1f) > BloomeryRules.FuelBurnFactor(0.5f));
        Assert.False(BloomeryRules.Burns(0f));
        Assert.True(BloomeryRules.Burns(BloomeryConfig.BurnSeconds));
    }
}
