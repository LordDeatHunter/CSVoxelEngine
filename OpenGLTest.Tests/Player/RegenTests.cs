using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Player;

/// <summary>Passive regeneration: out of combat only, slowed while sprinting, never past the pool.</summary>
public sealed class RegenTests
{
    private const float Step = 0.1f;

    private static void Run(PlayerHealth health, float seconds, bool sprinting = false, bool immune = false)
    {
        for (var t = 0f; t < seconds; t += Step)
            health.UpdateRegen(Step, sprinting, immune);
    }

    private static PlayerHealth Hurt(int damage = 50)
    {
        var health = new PlayerHealth();
        health.TakeDamage(damage);
        return health;
    }

    [Fact]
    public void NothingRegeneratesInsideTheCombatDelay()
    {
        var health = Hurt();
        Run(health, PlayerConfig.RegenDelaySeconds * 0.9f);
        Assert.Equal(PlayerHealth.MaxHealth - 50, health.Health);
    }

    [Fact]
    public void HealingStartsOnceTheDelayHasPassed()
    {
        var health = Hurt();
        Run(health, PlayerConfig.RegenDelaySeconds + 5f);
        Assert.True(health.Health > PlayerHealth.MaxHealth - 50);
    }

    [Fact]
    public void ADamageTakenRestartsTheDelay()
    {
        var health = Hurt();
        Run(health, PlayerConfig.RegenDelaySeconds * 0.9f);
        health.TakeDamage(10);
        var before = health.Health;
        Run(health, PlayerConfig.RegenDelaySeconds * 0.9f);
        Assert.Equal(before, health.Health);
    }

    [Fact]
    public void ABlowDealtRestartsTheDelay()
    {
        var health = Hurt();
        Run(health, PlayerConfig.RegenDelaySeconds * 0.9f);
        health.NoteCombat();
        var before = health.Health;
        Run(health, PlayerConfig.RegenDelaySeconds * 0.9f);
        Assert.Equal(before, health.Health);
    }

    [Fact]
    public void SprintingHealsSlowerThanWalking()
    {
        var walker = Hurt();
        var sprinter = Hurt();
        var seconds = PlayerConfig.RegenDelaySeconds + 10f;
        Run(walker, seconds);
        Run(sprinter, seconds, sprinting: true);
        Assert.True(sprinter.Health < walker.Health);
        Assert.True(sprinter.Health >= PlayerHealth.MaxHealth - 50);
    }

    [Fact]
    public void RegenNeverPassesTheFullPoolOrRaisesTheDead()
    {
        var hurt = Hurt(10);
        Run(hurt, 600f);
        Assert.Equal(PlayerHealth.MaxHealth, hurt.Health);

        var dead = Hurt(PlayerHealth.MaxHealth);
        Run(dead, 600f);
        Assert.True(dead.IsDead);
    }

    [Fact]
    public void AnImmunePlayerIsLeftAlone()
    {
        var health = Hurt();
        Run(health, 600f, immune: true);
        Assert.Equal(PlayerHealth.MaxHealth - 50, health.Health);
    }

    [Fact]
    public void ARespawnedPoolRestartsFromFull()
    {
        var health = Hurt(PlayerHealth.MaxHealth);
        health.Respawn();
        Assert.Equal(PlayerHealth.MaxHealth, health.Health);
        Run(health, PlayerConfig.RegenDelaySeconds * 2f);
        Assert.Equal(PlayerHealth.MaxHealth, health.Health);
    }
}
