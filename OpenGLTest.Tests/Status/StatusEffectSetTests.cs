using System;
using System.Collections.Generic;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Player;
using VoxelEngine.Status;
using Xunit;

namespace VoxelEngine.Tests.Status;

/// <summary>
/// The status effect runtime (<see cref="StatusEffectSet"/>) and the player's pool around it: damage
/// over time on the tick interval and only inside the duration, refresh without stacking, the strongest
/// slow winning, block actions gathered per tick, the client mirror, and the player's immunity, death and
/// respawn rules. Effects are compiled from in-memory definitions with explicit ids.
/// </summary>
public sealed class StatusEffectSetTests
{
    // Binary-exact steps, so tick counts are exact
    private const float Step = 0.25f;

    private static StatusEffect Effect(byte id, StatusEffectDefinition definition) =>
        StatusEffectLoader.Compile($"test{id}", definition, $"test{id}.json", id);

    private static StatusEffect Burn(byte id = 0, float duration = 5f, float tick = 1f, float share = 0.1f,
        string? deathMessage = null) =>
        Effect(id, new StatusEffectDefinition
        {
            DurationSeconds = duration, TickSeconds = tick, DamageShare = share, DeathMessage = deathMessage,
        });

    private static StatusEffect Slow(byte id, float duration, float move, float attack) =>
        Effect(id, new StatusEffectDefinition { DurationSeconds = duration, MoveFactor = move, AttackFactor = attack });

    private static StatusEffect Blocks(byte id, bool ignite, bool freeze) =>
        Effect(id, new StatusEffectDefinition
        {
            DurationSeconds = 5f, TickSeconds = 1f,
            Blocks = new StatusEffectDefinition.BlocksDefinition { Ignite = ignite, Freeze = freeze },
        });

    /// <summary>The simulated times (seconds since the apply) at which a tick dealt damage.</summary>
    private static List<float> DamageTimes(StatusEffectSet set, float seconds, int maxHealth)
    {
        var times = new List<float>();
        for (var t = Step; t <= seconds + 1e-4f; t += Step)
        {
            if (set.Tick(Step, maxHealth) > 0)
                times.Add(t);
        }
        return times;
    }

    [Fact]
    public void DamageTicksOnTheIntervalAndStopsAtTheDuration()
    {
        var burn = Burn();
        var set = new StatusEffectSet();
        Assert.True(set.Apply(burn, 1f));

        var times = DamageTimes(set, burn.DurationSeconds * 3f, maxHealth: 100);

        Assert.Equal((int)(burn.DurationSeconds / burn.TickSeconds), times.Count);
        Assert.True(times[0] >= burn.TickSeconds - 1e-4f, "the first tick waits one interval");
        for (var i = 1; i < times.Count; i++)
            Assert.Equal(burn.TickSeconds, times[i] - times[i - 1], 3);
        Assert.All(times, t => Assert.True(t <= burn.DurationSeconds + 1e-4f, "no tick after the duration"));
        Assert.False(set.IsActive(burn));
        Assert.Equal(0, set.Mask);
    }

    [Fact]
    public void AScaledDurationLastsLongerAndANonPositiveScaleIsRefused()
    {
        var burn = Burn();
        var set = new StatusEffectSet();
        Assert.False(set.Apply(burn, 0f));
        Assert.False(set.IsActive(burn));

        Assert.True(set.Apply(burn, 2f));
        Assert.True(set.RemainingSeconds(burn) > burn.DurationSeconds);
    }

    [Fact]
    public void DamagePerTickIsAtLeastOneAndGrowsWithMaxHealth()
    {
        Assert.True(StatusEffectSet.DamagePerTick(0.001f, 10) >= 1);
        foreach (var share in new[] { 0.01f, 0.05f, 0.3f, 1f })
        {
            var previous = 0;
            for (var maxHealth = 1; maxHealth <= 500; maxHealth += 7)
            {
                var damage = StatusEffectSet.DamagePerTick(share, maxHealth);
                Assert.True(damage >= 1);
                Assert.True(damage >= previous, "a larger pool never takes less");
                previous = damage;
            }
        }
    }

    [Fact]
    public void ReapplyingRefreshesTheDurationInsteadOfStackingDamage()
    {
        var burn = Burn();

        var once = new StatusEffectSet();
        once.Apply(burn, 1f);
        var thrice = new StatusEffectSet();
        thrice.Apply(burn, 1f);
        thrice.Apply(burn, 1f);
        thrice.Apply(burn, 1f);
        Assert.Equal(once.RemainingSeconds(burn), thrice.RemainingSeconds(burn));
        var onceDamage = DamageTimes(once, burn.DurationSeconds * 2f, 100).Count;
        var thriceDamage = DamageTimes(thrice, burn.DurationSeconds * 2f, 100).Count;
        Assert.Equal(onceDamage, thriceDamage);

        // Midway, a shorter application never cuts the time left and a full one tops it back up
        var set = new StatusEffectSet();
        set.Apply(burn, 1f);
        set.Tick(burn.DurationSeconds / 2f, 100);
        var left = set.RemainingSeconds(burn);
        set.Apply(burn, 0.1f);
        Assert.Equal(left, set.RemainingSeconds(burn));
        set.Apply(burn, 1f);
        Assert.Equal(burn.DurationSeconds, set.RemainingSeconds(burn));

        // A refresh keeps the tick clock: one interval still deals exactly one tick's damage
        var fresh = new StatusEffectSet();
        fresh.Apply(burn, 1f);
        fresh.Apply(burn, 1f);
        Assert.Equal(StatusEffectSet.DamagePerTick(burn.DamageShare, 100), fresh.Tick(burn.TickSeconds, 100));
    }

    [Fact]
    public void TheStrongestFactorWinsAndExpiryRestoresTheNext()
    {
        var shortSlow = Slow(0, duration: 2f, move: 0.3f, attack: 0.6f);
        var longSlow = Slow(1, duration: 6f, move: 0.7f, attack: 0.4f);
        var set = new StatusEffectSet();
        Assert.Equal(1f, set.MoveFactor);
        Assert.Equal(1f, set.AttackFactor);

        set.Apply(shortSlow, 1f);
        set.Apply(longSlow, 1f);
        Assert.Equal(Math.Min(shortSlow.MoveFactor, longSlow.MoveFactor), set.MoveFactor);
        Assert.Equal(Math.Min(shortSlow.AttackFactor, longSlow.AttackFactor), set.AttackFactor);

        set.Tick(shortSlow.DurationSeconds + Step, 100);
        Assert.False(set.IsActive(shortSlow));
        Assert.Equal(longSlow.MoveFactor, set.MoveFactor);
        Assert.Equal(longSlow.AttackFactor, set.AttackFactor);
        Assert.True(set.MoveFactor > shortSlow.MoveFactor, "the faster factor comes back");

        set.Tick(longSlow.DurationSeconds, 100);
        Assert.Equal(1f, set.MoveFactor);
        Assert.Equal(1f, set.AttackFactor);
    }

    [Fact]
    public void BlockActionsAccumulateOnTicksAndClearOnTake()
    {
        var ignite = Blocks(0, ignite: true, freeze: false);
        var freeze = Blocks(1, ignite: false, freeze: true);
        var set = new StatusEffectSet();
        set.Apply(ignite, 1f);
        set.Apply(freeze, 1f);

        set.Tick(ignite.TickSeconds / 2f, 100);
        Assert.Equal(StatusEffectBlockAction.None, set.TakeBlockActions());

        set.Tick(ignite.TickSeconds / 2f, 100);
        Assert.Equal(ignite.BlockActions | freeze.BlockActions, set.TakeBlockActions());
        Assert.Equal(StatusEffectBlockAction.None, set.TakeBlockActions());

        // A block-only effect never hurts
        Assert.Equal(0, set.Tick(ignite.TickSeconds, 100));
    }

    [Fact]
    public void AMirrorMaskDropsUnknownBits()
    {
        TestContent.EnsureLoaded();
        var set = new StatusEffectSet();
        set.SetMirrorMask(byte.MaxValue);
        Assert.Equal(StatusEffectRegistry.KnownMask, set.Mask);
        foreach (var effect in StatusEffectRegistry.All)
            Assert.True(set.IsActive(effect));

        // A mirror never ticks: no damage, and the mask stands until the server changes it
        Assert.Equal(0, set.Tick(60f, 100));
        Assert.Equal(StatusEffectRegistry.KnownMask, set.Mask);

        set.SetMirrorMask(0);
        Assert.Equal(0, set.Mask);
        Assert.Equal(1f, set.MoveFactor);
        Assert.False(set.TryGetTint(out _));
    }

    [Fact]
    public void AnImmuneOrDeadPlayerIsClearedAndUnhurt()
    {
        var burn = Burn();

        var immune = new PlayerHealth();
        immune.Effects.Apply(burn, 1f);
        immune.UpdateEffects(burn.DurationSeconds, immune: true);
        Assert.Equal(0, immune.Effects.Mask);
        Assert.Equal(PlayerHealth.MaxHealth, immune.Health);

        var dead = new PlayerHealth();
        dead.TakeDamage(PlayerHealth.MaxHealth, DamageSource.Fall);
        dead.Effects.Apply(burn, 1f);
        dead.UpdateEffects(burn.DurationSeconds, immune: false);
        Assert.Equal(0, dead.Effects.Mask);
        Assert.Equal(DamageSource.Fall, dead.LastSource);

        // The same effect hurts a survival player
        var survival = new PlayerHealth();
        survival.Effects.Apply(burn, 1f);
        survival.UpdateEffects(burn.TickSeconds, immune: false);
        Assert.True(survival.Health < PlayerHealth.MaxHealth);
        Assert.Equal(DamageSource.Magic, survival.LastSource);
    }

    [Fact]
    public void RespawnClearsEffects()
    {
        var burn = Burn();
        var health = new PlayerHealth();
        health.Effects.Apply(burn, 1f);
        health.Respawn();
        Assert.Equal(0, health.Effects.Mask);

        health.Effects.Apply(burn, 1f);
        health.Restore(PlayerHealth.MaxHealth);
        Assert.Equal(0, health.Effects.Mask);
    }

    [Fact]
    public void ALethalTickRecordsMagicAndThePhrase()
    {
        var lethal = Burn(share: 1f, deathMessage: "was consumed by the test");
        var health = new PlayerHealth();
        health.Effects.Apply(lethal, 1f);
        health.UpdateEffects(lethal.TickSeconds, immune: false);

        Assert.True(health.IsDead);
        Assert.Equal(DamageSource.Magic, health.LastSource);
        Assert.Equal(lethal.DeathMessage, health.LastKiller);
        Assert.Contains(lethal.DeathMessage!, DeathMessages.For("Player", health.LastSource, health.LastKiller));
    }

    [Fact]
    public void TheTintIsTheLowestIdTintedEffect()
    {
        var plain = Slow(0, 5f, 0.5f, 1f);
        var red = Effect(1, new StatusEffectDefinition { DurationSeconds = 5f, MoveFactor = 0.5f, Tint = [255, 0, 0] });
        var blue = Effect(2, new StatusEffectDefinition { DurationSeconds = 5f, MoveFactor = 0.5f, Tint = [0, 0, 255] });
        var set = new StatusEffectSet();
        set.Apply(plain, 1f);
        Assert.False(set.TryGetTint(out _));

        set.Apply(blue, 1f);
        set.Apply(red, 1f);
        Assert.True(set.TryGetTint(out var tint));
        Assert.Equal(red.TintLinear, tint);
    }
}
