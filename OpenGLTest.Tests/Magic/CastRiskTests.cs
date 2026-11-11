using VoxelEngine.Content.Spells;
using VoxelEngine.Magic;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Magic;

/// <summary>The fizzle roll and the backfire: pure rules, gear resistance cuts both, creative takes none.</summary>
public sealed class CastRiskTests
{
    private static SpellStats Stats(float fizzle) => new(SpellVerdict.Valid, 20f, 0f, fizzle, 1f, 1f);

    [Fact]
    public void ARollBelowTheChanceFizzlesAndAboveItDoesNot()
    {
        var stats = Stats(0.4f);
        Assert.True(CastRisk.Fizzles(stats, 0.39f));
        Assert.False(CastRisk.Fizzles(stats, 0.41f));
    }

    [Fact]
    public void ARecipeWithNoChanceNeverFizzles() => Assert.False(CastRisk.Fizzles(Stats(0f), 0f));

    [Fact]
    public void ResistanceCutsTheChance()
    {
        var stats = Stats(0.5f);
        Assert.True(CastRisk.Fizzles(stats, 0.4f));
        Assert.False(CastRisk.Fizzles(stats, 0.4f, resistance: 0.5f));
        Assert.False(CastRisk.Fizzles(stats, 0f, resistance: 1f));
    }

    [Fact]
    public void ARiskierRecipeBackfiresHarder()
    {
        var low = CastRisk.BackfireDamage(Stats(0.2f), PlayerHealth.MaxHealth, immune: false);
        var high = CastRisk.BackfireDamage(Stats(0.75f), PlayerHealth.MaxHealth, immune: false);
        Assert.True(low >= 1);
        Assert.True(high > low);
    }

    [Fact]
    public void CreativeAndFullResistanceTakeNoBackfire()
    {
        Assert.Equal(0, CastRisk.BackfireDamage(Stats(0.75f), PlayerHealth.MaxHealth, immune: true));
        Assert.Equal(0, CastRisk.BackfireDamage(Stats(0.75f), PlayerHealth.MaxHealth, immune: false, resistance: 1f));
    }
}
