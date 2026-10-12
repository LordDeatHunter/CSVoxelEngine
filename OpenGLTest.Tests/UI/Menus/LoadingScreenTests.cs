using System;
using System.Collections.Generic;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class LoadingScreenTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void ShownProgressEasesMonotonicallyAndNeverPassesTheTarget()
    {
        var view = new LoadingScreenView();
        view.Begin(LoadingScreenKind.Loading, "w", "c");

        var shown = new List<float>();
        var maxTarget = 0f;
        void Feed(float target, int steps)
        {
            for (var i = 0; i < steps; i++)
            {
                view.Update(target, false, Dt);
                maxTarget = MathF.Max(maxTarget, target);
                shown.Add(view.ShownProgress);
                Assert.True(view.ShownProgress <= maxTarget + 1e-6f);
            }
        }

        Feed(0.3f, 30);
        // Eased, not snapped: the first step lands strictly below its target.
        Assert.True(shown[0] < 0.3f);
        Feed(0.6f, 600);
        Assert.InRange(view.ShownProgress, 0.59f, 0.6f);
        Feed(0.2f, 60);

        for (var i = 1; i < shown.Count; i++)
            Assert.True(shown[i] >= shown[i - 1]);
        Assert.False(view.Complete);
    }

    [Fact]
    public void CompletionSnapsToFullThenFadesOut()
    {
        var fresh = new LoadingScreenView();
        Assert.True(fresh.IsFinished);

        var view = new LoadingScreenView();
        view.Begin(LoadingScreenKind.Loading, "w", "c");
        Assert.False(view.IsFinished);

        view.Update(0.4f, complete: true, 0f);
        Assert.Equal(1f, view.ShownProgress);
        Assert.True(view.Complete);
        Assert.Equal(1f, view.FadeAlpha);
        Assert.False(view.IsFinished);

        var elapsed = 0f;
        var previousAlpha = view.FadeAlpha;
        while (elapsed < WorldGenerationConfig.LoadingFadeSeconds * 2f)
        {
            view.Update(1f, true, Dt);
            elapsed += Dt;
            Assert.True(view.FadeAlpha <= previousAlpha);
            previousAlpha = view.FadeAlpha;
            if (elapsed < WorldGenerationConfig.LoadingFadeSeconds - Dt)
                Assert.False(view.IsFinished);
        }
        Assert.Equal(0f, view.FadeAlpha, 1e-5f);
        Assert.True(view.IsFinished);

        view.Begin(LoadingScreenKind.Saving, "w", "c");
        Assert.False(view.IsFinished);
        Assert.False(view.Complete);
        Assert.Equal(0f, view.ShownProgress);
        Assert.Equal(1f, view.FadeAlpha);

        view.Finish();
        Assert.True(view.IsFinished);
    }

    [Fact]
    public void SpinnerPhaseIsPeriodicAndBounded()
    {
        var period = WorldGenerationConfig.LoadingSpinnerPeriodSeconds;
        foreach (var t in new[] { 0.0, 0.37, 1.0, 5.5, 123.456, 10_000.25 })
        {
            var phase = LoadingScreenLayout.SpinnerPhase(t);
            Assert.InRange(phase, 0f, 0.999999f);
            Assert.Equal(phase, LoadingScreenLayout.SpinnerPhase(t + period), 1e-6f);
        }

        var previous = LoadingScreenLayout.SpinnerPhase(0.0);
        for (var i = 1; i < 100; i++)
        {
            var phase = LoadingScreenLayout.SpinnerPhase(period * i / 100.0);
            Assert.True(phase > previous);
            previous = phase;
        }
    }

    [Fact]
    public void TheLeadingDotIsTheBrightest()
    {
        var n = WorldGenerationConfig.LoadingSpinnerDots;
        for (var d = 0; d < n; d++)
        {
            // The middle of the phase slice that points at dot d.
            var phase = (d + 0.5f) / n;
            Assert.Equal(1f, LoadingScreenLayout.DotBrightness(d, n, phase));

            for (var other = 0; other < n; other++)
            {
                if (other == d) continue;
                var b = LoadingScreenLayout.DotBrightness(other, n, phase);
                Assert.True(b < 1f);
                Assert.True(b >= LoadingScreenLayout.DotBrightnessFloor);
            }

            var previous = 1f;
            var sawStrictDrop = false;
            for (var k = 1; k < n; k++)
            {
                var dot = ((d - k) % n + n) % n;
                var b = LoadingScreenLayout.DotBrightness(dot, n, phase);
                Assert.True(b <= previous);
                if (k == 1 && b < previous) sawStrictDrop = true;
                previous = b;
            }
            Assert.True(sawStrictDrop);
        }
    }
}
