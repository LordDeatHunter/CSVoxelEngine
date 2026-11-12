using VoxelEngine.Magic;
using Xunit;

namespace VoxelEngine.Tests.Magic;

/// <summary>The cast cooldown: a second cast waits out the cooldown, slack forgives jitter, asking never records.</summary>
public sealed class CastPacingTests
{
    private const double Start = 100.0;
    private static double Cooldown => SpellConfig.CastCooldownSeconds;

    [Fact]
    public void AFreshCasterIsReady() => Assert.True(new CastPacing().IsReady(0.0, 0.0));

    [Fact]
    public void ASecondCastWaitsOutTheCooldown()
    {
        var pacing = new CastPacing();
        pacing.Mark(Start);

        Assert.False(pacing.IsReady(Start, 0.0));
        Assert.False(pacing.IsReady(Start + Cooldown * 0.5, 0.0));
        Assert.True(pacing.IsReady(Start + Cooldown, 0.0));
        Assert.True(pacing.IsReady(Start + Cooldown * 2, 0.0));
    }

    [Fact]
    public void SlackLetsAnEarlyCastThrough()
    {
        var pacing = new CastPacing();
        pacing.Mark(Start);
        var early = Start + Cooldown * 0.75;

        Assert.False(pacing.IsReady(early, 0.0));
        Assert.True(pacing.IsReady(early, Cooldown * 0.25));
    }

    [Fact]
    public void AskingNeverRecords()
    {
        var pacing = new CastPacing();
        for (var i = 0; i < 5; i++)
            Assert.True(pacing.IsReady(Start, 0.0));

        pacing.Mark(Start);
        Assert.False(pacing.IsReady(Start + Cooldown * 0.5, 0.0));
        Assert.True(pacing.IsReady(Start + Cooldown, 0.0));
    }
}
