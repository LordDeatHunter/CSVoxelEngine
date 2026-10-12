using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <c>GpuTimingGate</c> is the GL-free gate in front of the per-pass <c>TIME_ELAPSED</c> queries:
/// it decides whether a frame issues them at all. The GL half — <c>BeginGpuPass</c>,
/// <c>EndGpuPass</c> and the three-deep drain ring — needs a live GL context that no test in this
/// suite has, so the <em>decision</em> is pinned here instead: no <c>RenderManager</c>, no window
/// and no GL anywhere in this file.
///
/// Every magnitude below is expressed as a multiple of
/// <c>WorldGenerationConfig.GpuTimingIdleInterval</c>, never as an arithmetic literal over it, so
/// retuning the duty cycle cannot turn a test into a tautology or into a spurious failure.
/// </summary>
public sealed class GpuTimingGateTests
{
    // long so that every comparison below is long-to-long: the frame counter the gate reads is a
    // long, and an int/long mismatch in Assert.Equal is a compile error, not a silent widening.
    private static long Interval => WorldGenerationConfig.GpuTimingIdleInterval;

    [Fact]
    public void OverlayOnTimesEveryFrame()
    {
        // While the X overlay is up the gate is wide open: this path is byte-identical to the
        // always-on behavior that shipped before the gate existed.
        var frames = 100L * Interval;
        for (var i = 0L; i < frames; i++)
        {
            Assert.True(GpuTimingGate.ShouldTime(overlayOn: true, frameIndex: i));
        }
    }

    [Fact]
    public void OverlayOffTimesOncePerIdleInterval()
    {
        // Deliberately not a whole number of intervals: the keep-alive count is a ceiling over the
        // frames walked, not a division, and the +3 tail is what catches an off-by-one there.
        var frames = 101L * Interval + 3L;
        var timed = 0L;
        for (var i = 0L; i < frames; i++)
        {
            if (GpuTimingGate.ShouldTime(overlayOn: false, frameIndex: i)) timed++;
        }

        Assert.Equal((frames + Interval - 1) / Interval, timed);
    }

    [Fact]
    public void OverlayOffNeverLeavesAPublishWindowWithoutSamples()
    {
        // This is the property R6's bullet claimed had to be given up: FrameProfiler rebuilds its
        // overlay rows only once a second in Publish, so what matters is that the snapshot standing
        // when X is pressed already carries GPU figures. An evenly spaced keep-alive guarantees
        // ceil(fps / Interval) samples per always-run pass in every publish window, so it does.
        // Nothing in the suite can press X — the visual harness has no key-press action — so this
        // Fact is the only coverage the overlay-off-to-on transition has.
        var frames = 128L * Interval;
        long lastTimed = -1;
        long minGap = long.MaxValue;
        long maxGap = long.MinValue;

        for (var i = 0L; i < frames; i++)
        {
            if (!GpuTimingGate.ShouldTime(overlayOn: false, frameIndex: i)) continue;
            if (lastTimed >= 0)
            {
                var gap = i - lastTimed;
                if (gap < minGap) minGap = gap;
                if (gap > maxGap) maxGap = gap;
            }

            lastTimed = i;
        }

        // Frame 0 times, so a run that has only just started is never left with an empty window.
        Assert.True(GpuTimingGate.ShouldTime(overlayOn: false, frameIndex: 0));
        // Evenly spaced, with no drift and no doubled-up frame: both extremes are the interval.
        Assert.Equal(Interval, maxGap);
        Assert.Equal(Interval, minGap);
    }
}
