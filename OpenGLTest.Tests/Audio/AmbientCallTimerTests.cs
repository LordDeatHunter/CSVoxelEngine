using System;
using VoxelEngine.Audio;
using Xunit;

namespace VoxelEngine.Tests.Audio;

public sealed class AmbientCallTimerTests
{
    [Fact]
    public void ItFiresOnlyWhenTheIntervalHasElapsedAndRearmsForTheNext()
    {
        var timer = new AmbientCallTimer(new Random(1), 2f, 2f);

        Assert.False(timer.Tick(1.9f));
        Assert.True(timer.Tick(0.2f));
        Assert.False(timer.Tick(1.9f));
        Assert.True(timer.Tick(0.2f));
    }
}
