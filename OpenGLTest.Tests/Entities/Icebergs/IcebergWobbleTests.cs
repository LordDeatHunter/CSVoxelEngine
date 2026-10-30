using System;
using VoxelEngine.Entities.Icebergs;
using Xunit;

namespace VoxelEngine.Tests.Entities.Icebergs;

public sealed class IcebergWobbleTests
{
    private static readonly int[] Seeds = [0, 1, 7, 12345, -9876, int.MaxValue];

    [Fact]
    public void TheWobbleStaysWithinItsAmplitude()
    {
        foreach (var seed in Seeds)
        foreach (var scale in new[] { IcebergConfig.ScaleMin, 1f, IcebergConfig.ScaleMax })
        {
            var damping = MathF.Pow(scale, IcebergConfig.WobbleScaleExponent);
            for (var t = 0.0; t < 120.0; t += 0.37)
            {
                var (bob, pitch, roll) = IcebergWobble.Sample(seed, t, scale);
                Assert.True(MathF.Abs(bob) <= IcebergConfig.BobBlocks / damping + 1e-6f);
                Assert.True(MathF.Abs(pitch) <= IcebergConfig.TiltRadians / damping + 1e-6f);
                Assert.True(MathF.Abs(roll) <= IcebergConfig.TiltRadians / damping + 1e-6f);
            }
        }
    }

    [Fact]
    public void ASmallTimeStepMovesItLittle()
    {
        // Each channel is the mean of two sines no faster than the shortest period, so its slope is
        // bounded by the amplitude times 2 pi over that period, even far along the clock.
        const double dt = 1.0 / 60.0;
        var rate = 2f * MathF.PI / IcebergConfig.WobblePeriodMinSeconds * (float)dt;
        foreach (var seed in Seeds)
        foreach (var start in new[] { 0.0, 50.0, 1e6 })
        {
            for (var t = start; t < start + 20.0; t += 0.5)
            {
                var a = IcebergWobble.Sample(seed, t, 1f);
                var b = IcebergWobble.Sample(seed, t + dt, 1f);
                Assert.True(MathF.Abs(b.Bob - a.Bob) <= IcebergConfig.BobBlocks * rate * 1.01f + 1e-5f);
                Assert.True(MathF.Abs(b.Pitch - a.Pitch) <= IcebergConfig.TiltRadians * rate * 1.01f + 1e-5f);
                Assert.True(MathF.Abs(b.Roll - a.Roll) <= IcebergConfig.TiltRadians * rate * 1.01f + 1e-5f);
            }
        }
    }

    [Fact]
    public void ABiggerBergWobblesNoMore()
    {
        foreach (var seed in Seeds)
        {
            var previous = (Bob: float.MaxValue, Tilt: float.MaxValue);
            for (var scale = IcebergConfig.ScaleMin; scale <= IcebergConfig.ScaleMax * 2f; scale += 0.25f)
            {
                var envelope = (Bob: 0f, Tilt: 0f);
                for (var t = 0.0; t < 60.0; t += 0.25)
                {
                    var (bob, pitch, roll) = IcebergWobble.Sample(seed, t, scale);
                    envelope.Bob = MathF.Max(envelope.Bob, MathF.Abs(bob));
                    envelope.Tilt = MathF.Max(envelope.Tilt, MathF.Max(MathF.Abs(pitch), MathF.Abs(roll)));
                }

                Assert.True(envelope.Bob <= previous.Bob);
                Assert.True(envelope.Tilt <= previous.Tilt);
                previous = envelope;
            }
        }
    }

    [Fact]
    public void ItIsAPureFunctionOfSeedAndTime()
    {
        foreach (var seed in Seeds)
            Assert.Equal(IcebergWobble.Sample(seed, 42.5, 1.1f), IcebergWobble.Sample(seed, 42.5, 1.1f));

        // Two seeds are not in lockstep.
        var differ = false;
        for (var t = 0.0; t < 30.0 && !differ; t += 0.5)
            differ = IcebergWobble.Sample(1, t, 1f) != IcebergWobble.Sample(2, t, 1f);
        Assert.True(differ);
    }
}
