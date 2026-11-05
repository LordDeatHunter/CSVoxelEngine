using System.Numerics;
using VoxelEngine.Particles;
using Xunit;

namespace VoxelEngine.Tests.Particles;

/// <summary>The water ripple rule (<see cref="WaterRipple"/>) and its store (<see cref="RippleSystem"/>).</summary>
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
            Assert.True(radius >= lastRadius, $"the ring shrank at {age}s");
            Assert.InRange(WaterRipple.OpacityAt(age, ripple.Life), 0f, 1f);
            lastRadius = radius;
        }

        Assert.Equal(ripple.MaxRadius, WaterRipple.RadiusAt(ripple.Life, ripple.Life, ripple.MaxRadius), 3);
        Assert.Equal(0f, WaterRipple.OpacityAt(ripple.Life, ripple.Life), 3);
        Assert.Equal(0f, WaterRipple.OpacityAt(0f, ripple.Life), 3);
    }

    [Fact]
    public void AHarderCrossingSpreadsWiderAndLastsLonger()
    {
        var soft = new WaterRipple(Vector3.Zero, 0.1f);
        var hard = new WaterRipple(Vector3.Zero, 0.9f);
        Assert.True(hard.MaxRadius > soft.MaxRadius);
        Assert.True(hard.Life > soft.Life);
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
    public void ARippleSitsJustOverTheWaterTop()
    {
        var system = new RippleSystem();
        system.Spawn(new Vector3(3f, 20f, 4f), 0.5f);
        Assert.InRange(system.Ripples[0].Position.Y - 20f, 0f, 0.1f);
    }
}
