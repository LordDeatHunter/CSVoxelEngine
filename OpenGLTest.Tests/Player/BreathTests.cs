using System.Numerics;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Player;

/// <summary>Drowning: the air drains only with the head under water, refills once it is out, and an
/// empty lung bills blows to the pool; creative and flight never drain, and a server bills the
/// player whose eye is under water through its own status.</summary>
public sealed class BreathTests
{
    public BreathTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 20f;

    private static void Run(Breath breath, float seconds, bool underWater, bool immune = false)
    {
        for (var t = 0f; t < seconds; t += Dt)
            breath.Step(Dt, underWater, immune);
    }

    [Fact]
    public void AirDrainsOnlyUnderWaterAndRefillsOnceTheHeadIsOut()
    {
        var breath = new Breath();
        Run(breath, 5f, underWater: false);
        Assert.Equal(1f, breath.Fraction);

        Run(breath, 5f, underWater: true);
        Assert.InRange(breath.Fraction, 0.01f, 0.99f);
        var drained = breath.Fraction;

        Run(breath, 0.5f, underWater: false);
        Assert.True(breath.Fraction > drained);
        Run(breath, 5f, underWater: false);
        Assert.Equal(1f, breath.Fraction);
    }

    [Fact]
    public void AnEmptyLungBillsBlowsUntilTheHeadSurfaces()
    {
        var health = new PlayerHealth();
        for (var t = 0f; t < 40f && !health.IsDead; t += Dt)
            health.UpdateBreath(Dt, eyeUnderWater: true, immune: false);
        Assert.True(health.IsDead);
        Assert.Equal(DamageSource.Drowning, health.LastSource);

        var survivor = new PlayerHealth();
        for (var t = 0f; t < 20f; t += Dt)
            survivor.UpdateBreath(Dt, true, false);
        var hp = survivor.Health;
        Assert.True(hp < PlayerHealth.MaxHealth);
        for (var t = 0f; t < 5f; t += Dt)
            survivor.UpdateBreath(Dt, false, false);
        Assert.Equal(hp, survivor.Health);
        Assert.Equal(1f, survivor.Breath.Fraction);
    }

    [Fact]
    public void CreativeAndFlightNeverDrown()
    {
        var health = new PlayerHealth();
        for (var t = 0f; t < 60f; t += Dt)
            health.UpdateBreath(Dt, eyeUnderWater: true, immune: true);
        Assert.Equal(PlayerHealth.MaxHealth, health.Health);
        Assert.Equal(1f, health.Breath.Fraction);
    }

    [Fact]
    public void TheWireStepKeepsAnyAirVisibleAndRoundTrips()
    {
        var breath = new Breath();
        Run(breath, 14.9f, underWater: true);
        Assert.True(breath.Fraction > 0f);
        Assert.True(breath.NetStep >= 1);

        var mirror = new Breath();
        mirror.SetNetStep(breath.NetStep);
        Assert.Equal(breath.NetStep, mirror.NetStep);
    }

    [Fact]
    public void AServerDrownsAPlayerWhoseEyeIsUnderWaterAndLetsADryOneBreathe()
    {
        var chunks = MobTestWorld.Pool(waterCells: 6);
        var eye = new Vector3(3.5f, 1f + Camera.EyeHeight, 3.5f);
        var diver = new ServerPlayer(1, "Diver", eye);
        var dry = new ServerPlayer(2, "Dry", new Vector3(3.5f, 8f + Camera.EyeHeight, 8.5f));
        var creative = new ServerPlayer(3, "Creative", eye) { Creative = true };

        for (var t = 0f; t < 20f; t += Dt)
        {
            diver.TickStatus(Dt, chunks);
            dry.TickStatus(Dt, chunks);
            creative.TickStatus(Dt, chunks);
        }

        Assert.True(diver.Status.Health < PlayerHealth.MaxHealth);
        Assert.True(diver.Status.Breath.NetStep < Breath.NetSteps);
        Assert.Equal(PlayerHealth.MaxHealth, dry.Status.Health);
        Assert.Equal(PlayerHealth.MaxHealth, creative.Status.Health);
    }
}
