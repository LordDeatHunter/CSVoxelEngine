using VoxelEngine.Config;
using VoxelEngine.Juice;
using Xunit;

namespace VoxelEngine.Tests.Juice;

/// <summary>The rules of the camera nudge a heavy mining hit gives (<see cref="BreakKick"/>).</summary>
public sealed class BreakKickTests
{
    [Fact]
    public void SoftBlocksGiveNoKick()
    {
        Assert.Equal(0f, BreakKick.Strength(PlayerConfig.BreakKickMinHardness, breaks: false));
        Assert.Equal(0f, BreakKick.Strength(0f, breaks: true));
    }

    [Fact]
    public void TheKickGrowsWithHardnessAndNeverPastItsCap()
    {
        var previous = 0f;
        for (var hardness = 0f; hardness <= 200f; hardness += 1f)
        {
            var kick = BreakKick.Strength(hardness, breaks: false);
            Assert.True(kick >= previous);
            Assert.True(kick <= PlayerConfig.BreakKickMaxStrength + 1e-6f);
            previous = kick;
        }
        Assert.True(previous > 0f);
    }

    [Fact]
    public void TheBreakingHitKicksHarderThanAChip()
    {
        var hardness = (PlayerConfig.BreakKickMinHardness + PlayerConfig.BreakKickFullHardness) / 2f;
        Assert.True(BreakKick.Strength(hardness, breaks: true) > BreakKick.Strength(hardness, breaks: false));
    }
}
