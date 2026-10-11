using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The discovery log behind the "New: ..." toast: a first credit reports, a repeat does not, and a
/// restored inventory is marked known so loading a world never toasts.
/// </summary>
public sealed class DiscoveryLogTests
{
    [Fact]
    public void TheFirstCreditReportsAndARepeatDoesNot()
    {
        var log = new DiscoveryLog();
        Assert.False(log.Knows(7));
        Assert.True(log.Discover(7));
        Assert.True(log.Knows(7));
        Assert.False(log.Discover(7));
        Assert.False(log.Discover(7));
    }

    [Fact]
    public void AMarkedIdNeverReportsAndAirIsNoFind()
    {
        var log = new DiscoveryLog();
        log.MarkKnown(9);
        Assert.True(log.Knows(9));
        Assert.False(log.Discover(9));

        // Air (id 0) is never a find, however often it is credited
        Assert.False(log.Discover(0));
        Assert.False(log.Knows(0));
    }

    [Fact]
    public void ResetForgetsEverything()
    {
        var log = new DiscoveryLog();
        log.Discover(3);
        log.MarkKnown(4);
        log.Reset();
        Assert.False(log.Knows(3));
        Assert.False(log.Knows(4));
        Assert.True(log.Discover(3));
    }
}
