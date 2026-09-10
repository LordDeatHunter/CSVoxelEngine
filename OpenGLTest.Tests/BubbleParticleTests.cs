using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class BubbleParticleTests
{
    public BubbleParticleTests() => TestContent.EnsureLoaded();

    private static byte WaterId => ContentRegistry.RequireBlock("water").Id;
    private static byte StoneId => ContentRegistry.RequireBlock("stone").Id;

    private static (ChunkManager Chunks, Chunk Center) WaterColumn(int top = 12)
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, 0, z, StoneId, 0);
        for (var x = 4; x < 12; x++)
        for (var z = 4; z < 12; z++)
        for (var y = 1; y <= top; y++)
            center.SetBlockRaw(x, y, z, WaterId, FluidState.Source);
        return (manager, center);
    }

    [Fact]
    public void BubbleRisesUpwards()
    {
        var (chunks, _) = WaterColumn();
        var bubble = new BubbleParticle(new Vector3(8.5f, 3f, 8.5f), new Random(1));
        var startY = bubble.Position.Y;

        Assert.True(bubble.Update(0.5f, chunks));
        Assert.True(bubble.Position.Y > startY);
    }

    [Fact]
    public void BubblePopsAtTheSurface()
    {
        var (chunks, _) = WaterColumn(top: 5);
        var bubble = new BubbleParticle(new Vector3(8.5f, 4.5f, 8.5f), new Random(2));

        var alive = true;
        for (var i = 0; i < 600 && alive; i++)
            alive = bubble.Update(1f / 60f, chunks);

        Assert.False(alive);
    }

    [Fact]
    public void BubbleAboveWaterPopsImmediately()
    {
        var (chunks, _) = WaterColumn(top: 5);
        var bubble = new BubbleParticle(new Vector3(8.5f, 7f, 8.5f), new Random(3));

        Assert.False(bubble.Update(1f / 60f, chunks));
    }

    [Fact]
    public void BubbleDiesAfterItsLifetime()
    {
        var (chunks, _) = WaterColumn(top: 15);
        var system = new BubbleParticleSystem();
        system.AddForTest(new BubbleParticle(new Vector3(8.5f, 2f, 8.5f), new Random(4)));

        // Far past BubbleLifetime: whatever it hits first (surface or timeout), it is gone.
        for (var i = 0; i < 1200 && system.Particles.Count > 0; i++)
            system.Update(1f / 60f, new Vector3(8.5f, 3f, 8.5f), -Vector3.UnitZ,
                false, chunks, new Random(4));

        Assert.Empty(system.Particles);
    }

    [Fact]
    public void SystemSpawnsOnlyWhileUnderwater()
    {
        var (chunks, _) = WaterColumn();
        var eye = new Vector3(8.5f, 5f, 8.5f);
        var rng = new Random(5);

        var dry = new BubbleParticleSystem();
        for (var i = 0; i < 120; i++)
            dry.Update(1f / 60f, eye, -Vector3.UnitZ, false, chunks, rng);
        Assert.Empty(dry.Particles);

        var wet = new BubbleParticleSystem();
        for (var i = 0; i < 120; i++)
            wet.Update(1f / 60f, eye, -Vector3.UnitZ, true, chunks, rng);
        Assert.NotEmpty(wet.Particles);
    }

    [Fact]
    public void SystemRespectsMaxParticles()
    {
        var (chunks, _) = WaterColumn(top: 15);
        var system = new BubbleParticleSystem();
        var rng = new Random(9);
        for (var i = 0; i < 3600; i++)
            system.Update(1f / 60f, new Vector3(8.5f, 5f, 8.5f), -Vector3.UnitZ,
                true, chunks, rng);

        Assert.True(system.Particles.Count <= WorldGenerationConfig.BubbleParticleMax);
        Assert.NotEmpty(system.Particles);
    }
}
