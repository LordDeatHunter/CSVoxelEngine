using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The held clock's rules (<see cref="Clock"/>): the day rolls over at midnight, the readout runs
/// 00:00..23:59 over one game day, and both follow the lighting clock the sun moves on.
/// </summary>
public sealed class ClockTests
{
    private const double Day = WorldGenerationConfig.DayLengthSeconds;

    [Fact]
    public void TheDayRollsOverAtMidnight()
    {
        // The first morning is day 1, the first midnight starts day 2, and each cycle adds one
        Assert.Equal(1, Clock.DayNumber(0.0));
        Assert.Equal(1, Clock.DayNumber(Clock.MidnightTime - 1.0));
        Assert.Equal(2, Clock.DayNumber(Clock.MidnightTime));
        Assert.Equal(2, Clock.DayNumber(Clock.MidnightTime + Day - 1.0));
        Assert.Equal(3, Clock.DayNumber(Clock.MidnightTime + Day));
    }

    [Fact]
    public void MidnightIsWhenTheSunStandsLowest()
    {
        var elevation = DayCycle.SunDirection(Clock.MidnightTime).Y;
        for (var t = 0.0; t < Day * 2.0; t += 7.0)
            Assert.True(DayCycle.SunDirection(t).Y >= elevation - 1e-4,
                $"the sun stands lower than midnight's at {t}: {DayCycle.SunDirection(t).Y}");

        // Noon is the other extreme, half a cycle from midnight
        Assert.True(DayCycle.SunDirection(Clock.MidnightTime + Day * 0.5).Y > 0.9f);
    }

    [Fact]
    public void TheReadoutRunsTheWholeDay()
    {
        Assert.Equal("00:00", Clock.Readout(Clock.MidnightTime));
        Assert.Equal("12:00", Clock.Readout(Clock.MidnightTime + Day * 0.5));
        Assert.Equal("23:59", Clock.Readout(Clock.MidnightTime + Day * (1439.5 / 1440.0)));

        // It wraps rather than running past 23:59, and is continuous across the rollover
        Assert.Equal("00:00", Clock.Readout(Clock.MidnightTime + Day));
        for (var t = 0.0; t < Day * 2.0; t += 13.0)
        {
            var readout = Clock.Readout(t);
            Assert.Matches(@"^([01]\d|2[0-3]):[0-5]\d$", readout);
        }
    }
}
