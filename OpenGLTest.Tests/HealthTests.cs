using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>First-cut survival: fall damage curve, lava burns, pool/flash behaviour.</summary>
public sealed class HealthTests
{
    public HealthTests()
    {
        TestContent.EnsureLoaded();
        if (GameSettings.FieldOfView <= 0f) GameSettings.ResetToDefaults();
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(4.99f, 0)]
    [InlineData(5f, 10)]
    [InlineData(5.9f, 10)]
    [InlineData(6f, 20)]
    [InlineData(12f, 80)]
    [InlineData(13f, 90)]
    [InlineData(30f, 100)]
    public void FallDamageScalesPastFiveBlocks(float drop, int expected) =>
        Assert.Equal(expected, PlayerHealth.FallDamage(drop));

    [Fact]
    public void TakeDamageClampsAtZeroAndMarksDead()
    {
        var health = new PlayerHealth();
        health.TakeDamage(30);
        Assert.Equal(70, health.Health);
        Assert.False(health.IsDead);

        health.TakeDamage(200);
        Assert.Equal(0, health.Health);
        Assert.True(health.IsDead);
    }

    [Fact]
    public void TakeDamageRaisesFlashAndRespawnKeepsItFading()
    {
        var health = new PlayerHealth();
        health.TakeDamage(10);
        Assert.Equal(1f, health.DamageFlash);

        health.Respawn();
        Assert.Equal(PlayerHealth.MaxHealth, health.Health);
        Assert.False(health.IsDead);
    }

    [Fact]
    public void LavaBurnsFiveHpPerHalfSecond()
    {
        var health = new PlayerHealth();
        health.UpdateLava(1.1f, inLava: true, immune: false);
        Assert.Equal(90, health.Health);
    }

    [Fact]
    public void LavaWaitsForFullTicksAndResetsOnExit()
    {
        var health = new PlayerHealth();
        health.UpdateLava(0.49f, inLava: true, immune: false);
        Assert.Equal(100, health.Health);

        health.UpdateLava(0.01f, inLava: true, immune: false);
        Assert.Equal(95, health.Health);

        // Leaving clears the partial tick, so coming back never eats an instant hit.
        health.UpdateLava(0.4f, inLava: false, immune: false);
        health.UpdateLava(0.4f, inLava: true, immune: false);
        Assert.Equal(95, health.Health);
    }

    [Fact]
    public void LavaImmunityTakesNoDamage()
    {
        var health = new PlayerHealth();
        health.UpdateLava(5f, inLava: true, immune: true);
        Assert.Equal(100, health.Health);
    }

    [Fact]
    public void DamageFlashDecaysToZero()
    {
        var health = new PlayerHealth();
        health.TakeDamage(10);
        health.Update(PlayerHealth.DamageFlashSeconds * 0.5f);
        Assert.Equal(0.5f, health.DamageFlash, precision: 5);
        health.Update(PlayerHealth.DamageFlashSeconds * 10f);
        Assert.Equal(0f, health.DamageFlash);
    }

    [Fact]
    public void CameraFlagsBodyInLava()
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;
        var lava = ContentRegistry.RequireBlock("lava").Id;
        chunk.SetBlockRaw(2, 0, 0, lava, 0);

        var camera = new Camera(null, null);
        var keys = new ScriptedKeyboard();
        camera.TestTeleport(new Vector3(2.5f, Camera.EyeHeight, 0.5f), yaw: 0f, pitch: 0f);
        camera.Update(1f / 60f, keys, manager);
        Assert.True(camera.IsInLava);

        camera.TestTeleport(new Vector3(8.5f, Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
        camera.Update(1f / 60f, keys, manager);
        Assert.False(camera.IsInLava);
    }
}
