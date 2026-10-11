using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Steam off hardening lava: <see cref="FluidManager.HardenedCells"/> reports both hardening paths
/// for the frame, and <see cref="FireParticleSystem.Puff"/> lays the smoke where it is asked to.
/// </summary>
public sealed class SteamPuffTests
{
    public SteamPuffTests() => TestContent.EnsureLoaded();

    private static ChunkManager FloorBasin()
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        return manager;
    }

    [Fact]
    public void LavaTouchingWaterIsReportedForTheFrameItHardened()
    {
        var manager = FloorBasin();
        var lava = ContentRegistry.RequireBlock("lava").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        var fluids = new FluidManager();
        var drops = new DropManager();
        manager.Chunks[Vector3.Zero].SetBlockRaw(8, 1, 8, lava, FluidState.Source);
        manager.Chunks[Vector3.Zero].SetBlockRaw(9, 1, 8, water, FluidState.Source);
        fluids.ScheduleAround(8, 1, 8);
        fluids.ScheduleAround(9, 1, 8);

        fluids.Update(1f, manager, drops);
        Assert.Contains(new Vector3(8, 1, 8), fluids.HardenedCells);
        // The water it hardened against is no hardening
        Assert.DoesNotContain(new Vector3(9, 1, 8), fluids.HardenedCells);

        // Nothing is left to harden, so the next frame reports nothing
        fluids.Update(1f, manager, drops);
        Assert.Empty(fluids.HardenedCells);
    }

    [Fact]
    public void AFlowArrivingHardenedIsReported()
    {
        var manager = FloorBasin();
        var lava = ContentRegistry.RequireBlock("lava").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        var fluids = new FluidManager();
        var drops = new DropManager();
        manager.Chunks[Vector3.Zero].SetBlockRaw(6, 1, 8, lava, FluidState.Source);
        manager.Chunks[Vector3.Zero].SetBlockRaw(8, 1, 8, water, FluidState.Source);
        fluids.ScheduleAround(6, 1, 8);
        fluids.ScheduleAround(8, 1, 8);

        fluids.Update(1f, manager, drops);

        Assert.Contains(new Vector3(7, 1, 8), fluids.HardenedCells);
        // A plain flow that did not harden is not steam
        Assert.True(manager.TryGetCell(9, 1, 8, out var spread, out _));
        Assert.Equal(water, spread);
        Assert.DoesNotContain(new Vector3(9, 1, 8), fluids.HardenedCells);
    }

    [Fact]
    public void APuffLaysItsSmokeAroundTheOriginAndRespectsTheCap()
    {
        var fire = new FireParticleSystem(0f, 0f, 16, Vector3.Zero);
        var origin = new Vector3(3.5f, 11f, -2.5f);
        const float spread = 0.8f;
        fire.Puff(origin, 5, spread, FireParticleKind.Steam, new Random(3));

        Assert.Equal(5, fire.Particles.Count);
        foreach (var particle in fire.Particles)
        {
            Assert.Equal(FireParticleKind.Steam, particle.Kind);
            Assert.True(MathF.Abs(particle.Position.X - origin.X) <= spread * 0.5f);
            Assert.True(MathF.Abs(particle.Position.Z - origin.Z) <= spread * 0.5f);
            Assert.True(particle.Position.Y >= origin.Y);
        }

        // Past the cap the oldest give way: the ring is full of the newest puff's smoke
        fire.Puff(origin, 40, spread, FireParticleKind.Smoke, new Random(4));
        Assert.Equal(16, fire.Particles.Count);
        Assert.All(fire.Particles, p => Assert.Equal(FireParticleKind.Smoke, p.Kind));
    }

    [Fact]
    public void SteamRisesAndFadesOutAndIsDrawnBlendedLikeSmoke()
    {
        var rng = new Random(11);
        var steam = new FireParticle(new Vector3(0f, 5f, 0f), FireParticleKind.Steam, rng);
        var start = steam.Position.Y;
        var startAlpha = steam.Alpha;
        var steps = 0;
        while (steam.Update(1f / 60f) && steps < 6000) steps++;

        Assert.False(steam.IsAlive);
        Assert.True(steam.Position.Y > start);
        Assert.True(startAlpha > 0f);
        Assert.Equal(0f, steam.Alpha, 3);

        // Anything but an ember goes in the blended smoke run of the draw order
        var kinds = new[] { FireParticleKind.Steam, FireParticleKind.Ember, FireParticleKind.Smoke };
        var order = new int[3];
        var embers = FireParticleRenderer.OrderForDraw(kinds, new float[] { 1f, 2f, 3f }, order);
        Assert.Equal(1, embers);
        Assert.Equal(1, order[0]);
    }
}
