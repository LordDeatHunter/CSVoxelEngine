using System;
using System.Numerics;
using VoxelEngine.Particles;
using Xunit;

namespace VoxelEngine.Tests.Particles;

/// <summary>The water ripple rule (<see cref="WaterRipple"/>), its wave packet (<see cref="RippleWave"/>),
/// its store (<see cref="RippleSystem"/>) and what the water shader is handed (<see cref="RippleField"/>).</summary>
public sealed class WaterRippleTests
{
    [Fact]
    public void TheRingOnlyEverSpreadsAndTheOpacityIsGoneExactlyAtTheEnd()
    {
        var ripple = new WaterRipple(Vector3.Zero, 0.5f);
        var lastRadius = -1f;
        for (var age = 0f; age <= ripple.Life; age += ripple.Life / 50f)
        {
            var radius = WaterRipple.RadiusAt(age, ripple.Life, ripple.MaxRadius);
            Assert.True(radius > lastRadius, $"the ring stalled or shrank at {age}s");
            Assert.InRange(WaterRipple.OpacityAt(age, ripple.Life), 0f, 1f);
            lastRadius = radius;
        }

        Assert.Equal(ripple.MaxRadius, WaterRipple.RadiusAt(ripple.Life, ripple.Life, ripple.MaxRadius), 3);
        Assert.Equal(0f, WaterRipple.OpacityAt(ripple.Life, ripple.Life), 3);
        Assert.Equal(0f, WaterRipple.OpacityAt(0f, ripple.Life), 3);
    }

    [Fact]
    public void AHarderCrossingSpreadsWiderLastsLongerAndStandsSteeper()
    {
        var soft = new WaterRipple(Vector3.Zero, 0.1f);
        var hard = new WaterRipple(Vector3.Zero, 0.9f);
        Assert.True(hard.MaxRadius > soft.MaxRadius);
        Assert.True(hard.Life > soft.Life);
        Assert.True(hard.PeakSlope > soft.PeakSlope);
    }

    [Fact]
    public void ASmallBodyMakesFinerWavesThanABigOne()
    {
        var pebble = new WaterRipple(Vector3.Zero, 0.5f, sizeScale: 0.4f);
        var boulder = new WaterRipple(Vector3.Zero, 0.5f, sizeScale: 2f);
        Assert.True(pebble.Wavelength < boulder.Wavelength);
    }

    [Fact]
    public void TheWaterIsCalmAheadOfTheEdgeAndAgainBehindTheTrain()
    {
        for (var phase = 0f; phase < 1f; phase += 0.125f)
        {
            Assert.Equal(0f, RippleWave.Slope(-ParticleConfig.RippleLeadWaves - 0.01f, phase));
            Assert.Equal(0f, RippleWave.Slope(-5f, phase));
            Assert.Equal(0f, RippleWave.Slope(ParticleConfig.RippleTrainWaves + 0.01f, phase));
            Assert.Equal(0f, RippleWave.Slope(ParticleConfig.RippleTrainWaves * 3f, phase));
        }
    }

    [Fact]
    public void ThePacketStandsTallestJustBehindTheEdgeAndDiesTowardTheMiddle()
    {
        var train = ParticleConfig.RippleTrainWaves;
        var peakAt = 0f;
        var peak = 0f;
        for (var u = -ParticleConfig.RippleLeadWaves; u <= train; u += 0.01f)
        {
            var e = RippleWave.Envelope(u);
            Assert.InRange(e, 0f, 1f);
            if (e <= peak) continue;
            peak = e;
            peakAt = u;
        }

        Assert.True(peak > 0.5f, "the packet never stands up");
        Assert.InRange(peakAt, 0f, train * 0.5f);
        // Past the peak the train only ever dies away
        var last = peak;
        for (var u = peakAt; u <= train; u += 0.01f)
        {
            var e = RippleWave.Envelope(u);
            Assert.True(e <= last + 1e-5f, $"the train swelled again {u} waves behind the edge");
            last = e;
        }
    }

    [Fact]
    public void TheTrainHoldsSeveralCrestsAndTroughsThatNeverPassTheEnvelope()
    {
        var crossings = 0;
        var last = 0f;
        for (var u = 0f; u <= ParticleConfig.RippleTrainWaves; u += 0.005f)
        {
            var s = RippleWave.Slope(u, 0f);
            Assert.True(MathF.Abs(s) <= RippleWave.Envelope(u) + 1e-5f);
            if (s * last < 0f) crossings++;
            if (s != 0f) last = s;
        }

        Assert.True(crossings >= 4, $"only {crossings} crest/trough changes: a single ring, not a train");
    }

    [Fact]
    public void TheWavesGetShorterTowardTheBackOfTheTrain()
    {
        // Zero crossings of the slope, front to back: the gaps between them only ever close up
        var zeros = new System.Collections.Generic.List<float>();
        var last = RippleWave.Slope(0.05f, 0f);
        for (var u = 0.05f; u < ParticleConfig.RippleTrainWaves - 0.05f; u += 0.001f)
        {
            var s = RippleWave.Slope(u, 0f);
            if (s * last < 0f) zeros.Add(u);
            if (s != 0f) last = s;
        }

        Assert.True(zeros.Count >= 4);
        for (var i = 2; i < zeros.Count; i++)
            Assert.True(zeros[i] - zeros[i - 1] < zeros[i - 1] - zeros[i - 2] + 0.002f,
                $"the wave at {zeros[i]} is longer than the one ahead of it");
    }

    [Fact]
    public void TheCrestsRunForwardThroughThePacketAsTheRippleAges()
    {
        // The crest nearest the edge sits closer to it a moment later: the phase a ripple gains with
        // age carries its crests toward the leading edge
        var ripple = new WaterRipple(Vector3.Zero, 0.5f);
        var before = ripple.Phase;
        ripple.Update(0.05f);
        Assert.True(ripple.Phase > before);
        Assert.True(FirstCrest(ripple.Phase) < FirstCrest(before));
    }

    // Wavelengths behind the edge of the first downward zero crossing after the packet has risen
    private static float FirstCrest(float phase)
    {
        var last = RippleWave.Slope(1f, phase);
        for (var u = 1f; u < ParticleConfig.RippleTrainWaves; u += 0.001f)
        {
            var s = RippleWave.Slope(u, phase);
            if (last > 0f && s <= 0f) return u;
            last = s;
        }

        return float.NaN;
    }

    [Fact]
    public void ARippleThinsAsItSpreadsAndIsFlatAtBirthAndAtTheEnd()
    {
        Assert.Equal(1f, WaterRipple.SpreadShare(0f), 3);
        var lastShare = 1f;
        for (var radius = 0.25f; radius < 8f; radius += 0.25f)
        {
            var share = WaterRipple.SpreadShare(radius);
            Assert.InRange(share, 0f, lastShare);
            lastShare = share;
        }

        Assert.True(lastShare < 0.5f, "a wide ring still stands as tall as a tight one");

        var ripple = new WaterRipple(Vector3.Zero, 1f);
        Assert.Equal(0f, ripple.Slope, 3);
        ripple.Update(ripple.Life * 0.3f);
        Assert.True(ripple.Slope > 0f);
        ripple.Update(ripple.Life * 0.7f);
        Assert.Equal(0f, ripple.Slope, 3);
    }

    [Fact]
    public void ARippleExpiresAndTheStoreNeverPassesItsCap()
    {
        var system = new RippleSystem();
        for (var i = 0; i < ParticleConfig.RippleMax + 10; i++)
            system.Spawn(new Vector3(i, 0f, 0f), 1f);
        Assert.Equal(ParticleConfig.RippleMax, system.Ripples.Count);

        for (var i = 0; i < 200; i++)
            system.Update(0.05f);
        Assert.Empty(system.Ripples);
    }

    [Fact]
    public void TheShaderIsHandedTheRipplesNearestTheEyeFirst()
    {
        var system = new RippleSystem();
        var total = ParticleConfig.RippleShaded + 6;
        // Spawned far to near, so the kept ones are not simply the first or the last in the store
        for (var i = 0; i < total; i++)
            system.Spawn(new Vector3((i * 7) % total * 3f, 10f, 0f), 0.5f);
        system.Update(0.2f);

        var field = new RippleField();
        var eye = new Vector3(-1f, 12f, 0f);
        field.Pack(system.Ripples, eye);

        Assert.Equal(ParticleConfig.RippleShaded, field.Count);
        var farthestKept = 0f;
        var last = -1f;
        foreach (var shape in field.Shapes)
        {
            Assert.Equal(10f, shape.Y);
            Assert.True(shape.X >= last, "the packed ripples are not nearest first");
            last = shape.X;
            farthestKept = shape.X;
        }

        // Every ripple left out lies farther from the eye than every one kept
        Assert.Equal((ParticleConfig.RippleShaded - 1) * 3f, farthestKept, 3);
    }

    [Fact]
    public void AnEmptyWaterHandsTheShaderNothing()
    {
        var system = new RippleSystem();
        system.Spawn(Vector3.Zero, 1f);
        var field = new RippleField();
        field.Pack(system.Ripples, Vector3.UnitY);
        Assert.Equal(1, field.Count);

        for (var i = 0; i < 200; i++)
            system.Update(0.05f);
        field.Pack(system.Ripples, Vector3.UnitY);
        Assert.Equal(0, field.Count);
        Assert.True(field.Shapes.IsEmpty);
    }
}
