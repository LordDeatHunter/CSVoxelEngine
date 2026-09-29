using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The player's save-restore seams: <see cref="Camera.RestoreState"/> sets pose and modes with
/// flight implying creative, <see cref="PlayerHealth.Restore"/> clamps into the pool without a
/// damage flash, and <see cref="Camera.Detach"/> is safe to call repeatedly.
/// </summary>
public sealed class PlayerRestoreTests
{
    public PlayerRestoreTests()
    {
        TestContent.EnsureLoaded();
        if (GameSettings.FieldOfView <= 0f) GameSettings.ResetToDefaults();
    }

    [Fact]
    public void RestoreStateSetsPoseAndModesConsistently()
    {
        var camera = new Camera(null, null);
        var position = new Vector3(3f, 80f, -7f);

        camera.RestoreState(position, 33f, 200f, creative: false, flying: true);
        Assert.Equal(position, camera.Position);
        Assert.Equal(33f, camera.Yaw);
        Assert.Equal(MathUtils.ClampPitch(200f), camera.Pitch);
        Assert.True(camera.IsFlying);
        Assert.True(camera.IsCreative);
        Assert.Equal(0f, camera.VerticalVelocity);

        camera.RestoreState(position, 33f, 0f, creative: true, flying: false);
        Assert.True(camera.IsCreative);
        Assert.False(camera.IsFlying);

        camera.RestoreState(position, 33f, 0f, creative: false, flying: false);
        Assert.False(camera.IsCreative);
        Assert.False(camera.IsFlying);
    }

    [Fact]
    public void HealthRestoreClampsAndClearsTheFlash()
    {
        var health = new PlayerHealth();
        health.TakeDamage(30);
        Assert.True(health.DamageFlash > 0f);

        health.Restore(42);
        Assert.Equal(42, health.Health);
        Assert.Equal(0f, health.DamageFlash);

        health.Restore(-5);
        Assert.Equal(0, health.Health);
        Assert.True(health.IsDead);

        health.Restore(PlayerHealth.MaxHealth * 3);
        Assert.Equal(PlayerHealth.MaxHealth, health.Health);
    }

    [Fact]
    public void DetachIsIdempotent()
    {
        var camera = new Camera(null, null);
        camera.Detach();
        camera.Detach();
    }
}
