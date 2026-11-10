using System.Numerics;
using VoxelEngine.Particles;
using VoxelEngine.Simulation.Fluids;
using Xunit;

namespace VoxelEngine.Tests.Particles;

/// <summary>The water ripple rule (<see cref="WaterRipple"/>) and its store (<see cref="RippleSystem"/>).</summary>
public sealed class WaterRippleTests
{
    public WaterRippleTests() => TestContent.EnsureLoaded();

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

    [Fact]
    public void ARingNeverLeavesTheWaterItWasBornOn()
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        var chunk = manager.Chunks[Vector3.Zero];
        var water = ContentRegistry.RequireBlock("water").Id;
        for (var x = 2; x < 6; x++)
        for (var z = 2; z < 6; z++)
            chunk.SetBlockRaw(x, 1, z, water, FluidState.Source);
        chunk.SetBlockRaw(10, 1, 10, water, FluidState.Source);

        var top = manager.GetFluidColumnTopGlobal(3, 1, 3);
        var pool = RippleSurface.Gather(manager, new Vector3(3.5f, top, 3.5f), top, 3f);
        Assert.Equal((3, 3), (pool[0].X, pool[0].Z));
        Assert.Equal(16, pool.Count);
        Assert.All(pool, c => Assert.InRange(c.X, 2, 5));

        var lone = RippleSurface.Gather(manager, new Vector3(10.5f, top, 10.5f), top, 3f);
        Assert.Single(lone);
    }

    [Fact]
    public void ARingSpreadsOverACellOnlyWhenItsReachTouchesIt()
    {
        var center = new Vector3(0.5f, 0f, 0.5f);
        Assert.True(RippleSurface.ReachesCell(center, 0, 0, 0.1f));
        Assert.True(RippleSurface.ReachesCell(center, 1, 0, 0.6f));
        Assert.False(RippleSurface.ReachesCell(center, 1, 0, 0.4f));
        Assert.False(RippleSurface.ReachesCell(center, 2, 2, 1.5f));
    }
}
