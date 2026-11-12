using System.Numerics;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Player;

/// <summary>The mana pool: it refills slowly up to its max, a cast is refused when short and free in
/// creative, the max grows with known parts up to its cap, a dead player does not regenerate, the wire
/// values mirror and clamp, and a server reports a regenerating pool only when its wire value moves.</summary>
public sealed class ManaTests
{
    public ManaTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 20f;

    [Fact]
    public void RegenRefillsASpentPoolAndStopsAtTheMax()
    {
        var mana = new PlayerMana();
        Assert.True(mana.TrySpend(mana.Max * 0.75f, creative: false));
        var previous = mana.Current;
        for (var t = 0f; t < ManaConfig.RegenFullSeconds + 1f; t += Dt)
        {
            mana.Regen(Dt);
            Assert.True(mana.Current >= previous);
            Assert.True(mana.Current <= mana.Max);
            previous = mana.Current;
        }
        Assert.Equal(mana.Max, mana.Current);
    }

    [Fact]
    public void AShortSpendIsRefusedAndChangesNothing()
    {
        var mana = new PlayerMana();
        Assert.True(mana.TrySpend(mana.Max * 0.9f, creative: false));
        var left = mana.Current;
        Assert.False(mana.TrySpend(left + 1f, creative: false));
        Assert.Equal(left, mana.Current);
        Assert.True(mana.TrySpend(left, creative: false));
        Assert.Equal(0f, mana.Current);
    }

    [Fact]
    public void CreativeSpendsForFree()
    {
        var mana = new PlayerMana();
        var before = mana.Current;
        Assert.True(mana.TrySpend(mana.Max * 2f, creative: true));
        Assert.Equal(before, mana.Current);
    }

    [Fact]
    public void ANonFiniteOrNegativeCostNeverChangesThePool()
    {
        var mana = new PlayerMana();
        mana.TrySpend(mana.Max / 3f, creative: false);
        var before = mana.Current;
        Assert.False(mana.TrySpend(float.NaN, creative: false));
        Assert.False(mana.TrySpend(float.PositiveInfinity, creative: false));
        Assert.True(mana.TrySpend(-5f, creative: false));
        Assert.Equal(before, mana.Current);
    }

    [Fact]
    public void TheMaxGrowsWithKnownPartsUpToTheCap()
    {
        Assert.Equal(PlayerMana.MaxFor(0), new PlayerMana().Max);
        var previous = PlayerMana.MaxFor(0);
        for (var n = 1; n <= 1000; n++)
        {
            var max = PlayerMana.MaxFor(n);
            Assert.True(max >= previous);
            Assert.True(max <= ManaConfig.MaxCap);
            previous = max;
        }
        Assert.True(PlayerMana.MaxFor(1) > PlayerMana.MaxFor(0));
        Assert.Equal(PlayerMana.MaxFor(1000), PlayerMana.MaxFor(2000));

        // A larger pool never refills the mana already there
        var mana = new PlayerMana();
        var before = mana.Current;
        mana.SetKnownParts(1000);
        Assert.Equal(PlayerMana.MaxFor(1000), mana.Max);
        Assert.Equal(before, mana.Current);

        // Once the mana grew past the small pool, shrinking the pool clamps it down
        mana.Refill();
        Assert.True(mana.Current > PlayerMana.MaxFor(0));
        mana.SetKnownParts(0);
        Assert.Equal(mana.Max, mana.Current);
    }

    [Fact]
    public void ADeadPlayerDoesNotRegenerateAndARespawnRefills()
    {
        var health = new PlayerHealth();
        health.Mana.TrySpend(health.Mana.Max / 2f, creative: false);
        health.TakeDamage(PlayerHealth.MaxHealth);
        Assert.True(health.IsDead);
        var spent = health.Mana.Current;
        for (var t = 0f; t < 10f; t += Dt)
            health.UpdateMana(Dt);
        Assert.Equal(spent, health.Mana.Current);

        health.Respawn();
        Assert.Equal(1f, health.Mana.Fraction);
    }

    [Fact]
    public void TheWireValuesRoundTripIntoAMirrorAndClamp()
    {
        var source = new PlayerMana();
        source.SetKnownParts(3);
        source.TrySpend(source.Max / 3f + 0.5f, creative: false);
        var mirror = new PlayerMana();
        mirror.SetNet(source.NetMana, source.Max);
        Assert.Equal(source.NetMana, mirror.NetMana);
        Assert.Equal(source.Max, mirror.Max);
        Assert.True(mirror.Current <= source.Current);

        mirror.SetNet(mirror.Max + 50, mirror.Max);
        Assert.Equal(mirror.Max, mirror.Current);
        mirror.SetNet(10, PlayerMana.WireMaxCap + 1000);
        Assert.Equal(PlayerMana.WireMaxCap, mirror.Max);
        mirror.SetNet(10, 0);
        Assert.Equal(1, mirror.Max);
        Assert.Equal(1f, mirror.Current);
        mirror.SetNet(-4, 50);
        Assert.Equal(0f, mirror.Current);
    }

    [Fact]
    public void RestoreClampsASavedValueIntoThePool()
    {
        var mana = new PlayerMana();
        mana.Restore(-5f);
        Assert.Equal(0f, mana.Current);
        mana.Restore(1e9f);
        Assert.Equal(mana.Max, mana.Current);
        mana.Restore(0f);
        mana.Restore(float.NaN);
        Assert.Equal(mana.Max, mana.Current);
    }

    [Fact]
    public void ADrainEmptiesThePoolWhichThenRefillsAsUsual()
    {
        var mana = new PlayerMana();
        mana.Drain();
        Assert.Equal(0f, mana.Current);
        Assert.False(mana.MoreThanHalf);

        mana.Regen(1f);
        Assert.True(mana.Current > 0f);
    }

    [Fact]
    public void MoreThanHalfIsStrictlyAboveHalfInWholeUnits()
    {
        var mana = new PlayerMana();
        Assert.True(mana.MoreThanHalf);

        // Exactly half is not enough, and neither is a fraction of a unit past it
        mana.Restore(mana.Max / 2);
        Assert.False(mana.MoreThanHalf);
        mana.Restore(mana.Max / 2 + 0.9f);
        Assert.False(mana.MoreThanHalf);
        mana.Restore(mana.Max / 2 + 1);
        Assert.True(mana.MoreThanHalf);
    }

    [Fact]
    public void AServerRegeneratesManaAndReportsOnlyWhenTheWireValueMoves()
    {
        var chunks = MobTestWorld.Pool(waterCells: 6);
        var dry = new Vector3(3.5f, 8f + Camera.EyeHeight, 8.5f);
        var full = new ServerPlayer(1, "Full", dry) { StatusDirty = false };
        var caster = new ServerPlayer(2, "Caster", dry);
        Assert.True(caster.Status.Mana.TrySpend(caster.Status.Mana.Max / 2f, creative: false));
        caster.StatusDirty = false;
        var spent = caster.Status.Mana.NetMana;

        for (var t = 0f; t < 5f; t += Dt)
        {
            full.TickStatus(Dt, chunks);
            caster.TickStatus(Dt, chunks);
        }

        Assert.False(full.StatusDirty);
        Assert.True(caster.Status.Mana.NetMana > spent);
        Assert.True(caster.StatusDirty);
    }
}
