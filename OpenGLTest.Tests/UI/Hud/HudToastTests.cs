using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The HUD toast and its dawn watch: a line holds opaque then fades out, a new line restarts the
/// clock, and the day watch fires once as the sun rises through dawn, never on a jump.
/// </summary>
public sealed class HudToastTests
{
    [Fact]
    public void ALineHoldsThenFadesOut()
    {
        var toast = new HudToast();
        Assert.Equal(0f, toast.Alpha);

        toast.Show("Day 4");
        Assert.Equal("Day 4", toast.Text);
        Assert.Equal(1f, toast.Alpha);

        // Opaque through the hold
        toast.Update(HudToast.HoldSeconds);
        Assert.Equal(1f, toast.Alpha);
        Assert.Equal("Day 4", toast.Text);

        // Half way into the fade it is dimmed but visible
        toast.Update(HudToast.FadeSeconds * 0.5f);
        Assert.True(toast.Alpha > 0f && toast.Alpha < 1f, $"the toast read {toast.Alpha}");

        // Past the fade it is gone, and updating again changes nothing
        toast.Update(HudToast.FadeSeconds);
        Assert.Equal(0f, toast.Alpha);
        Assert.Equal("", toast.Text);
        toast.Update(HudToast.HoldSeconds);
        Assert.Equal(0f, toast.Alpha);
    }

    [Fact]
    public void ANewLineRestartsTheClock()
    {
        var toast = new HudToast();
        toast.Show("Day 4");
        toast.Update(HudToast.HoldSeconds + HudToast.FadeSeconds);
        Assert.Equal(0f, toast.Alpha);

        toast.Show("New: Cotton");
        Assert.Equal(1f, toast.Alpha);
        Assert.Equal("New: Cotton", toast.Text);
    }

    [Fact]
    public void TheDayWatchFiresOnceAtDawnAndNeverOnAJump()
    {
        // Rising through the dawn line counts
        Assert.True(DayToast.CrossedDawn(DayToast.DawnElevation - 0.01f, DayToast.DawnElevation + 0.01f));
        // Already above it, or falling through it, does not
        Assert.False(DayToast.CrossedDawn(DayToast.DawnElevation + 0.01f, DayToast.DawnElevation + 0.02f));
        Assert.False(DayToast.CrossedDawn(DayToast.DawnElevation + 0.01f, DayToast.DawnElevation - 0.01f));
        // A jump that lands above the line without crossing it (a clock set) does not fire
        Assert.False(DayToast.CrossedDawn(0.5f, 0.6f));

        // The real clock crosses dawn exactly once per day
        var day = WorldGenerationConfig.DayLengthSeconds;
        var crossings = 0;
        var previous = DayCycle.SunDirection(0.0).Y;
        for (var t = 0.0; t < day * 2.0; t += 1.0)
        {
            var elevation = DayCycle.SunDirection(t).Y;
            if (DayToast.CrossedDawn(previous, elevation)) crossings++;
            previous = elevation;
        }
        Assert.Equal(2, crossings);
    }
}
