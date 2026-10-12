using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Player;

/// <summary>
/// The death message (<see cref="DeathMessages"/>): the line names the killer for a mob, and the
/// source for a fall, lava or a cactus; an unnamed mob death drops the "by X" clause.
/// </summary>
public sealed class DeathMessageTests
{
    [Fact]
    public void TheLineNamesTheKillerForAMob()
    {
        Assert.Equal("Player was slain by Goblin",
            DeathMessages.For("Player", DamageSource.Mob, "Goblin"));
        Assert.Equal("Player was slain by Slime",
            DeathMessages.For("Player", DamageSource.Mob, "Slime"));
        // An unnamed mob death still reads
        Assert.Equal("Player was slain", DeathMessages.For("Player", DamageSource.Mob, null));
        Assert.Equal("Player was slain", DeathMessages.For("Player", DamageSource.Mob, ""));
    }

    [Fact]
    public void TheLineNamesTheOtherSources()
    {
        Assert.Equal("Player fell", DeathMessages.For("Player", DamageSource.Fall, null));
        Assert.Equal("Player burned in lava", DeathMessages.For("Player", DamageSource.Lava, null));
        Assert.Equal("Player was pricked to death", DeathMessages.For("Player", DamageSource.Cactus, null));
        Assert.Equal("Player died", DeathMessages.For("Player", DamageSource.None, null));
        // A named killer never leaks into a non-mob line
        Assert.Equal("Player fell", DeathMessages.For("Player", DamageSource.Fall, "Goblin"));
    }

    [Fact]
    public void TheHealthPoolRemembersTheLastNamedBlow()
    {
        var health = new PlayerHealth();
        health.TakeDamage(5);
        Assert.Equal(DamageSource.None, health.LastSource);

        health.TakeDamage(5, DamageSource.Mob, "Goblin");
        Assert.Equal(DamageSource.Mob, health.LastSource);
        Assert.Equal("Goblin", health.LastKiller);

        // A later source-less hit leaves the record standing
        health.TakeDamage(5);
        Assert.Equal(DamageSource.Mob, health.LastSource);

        // Lava names itself
        health.UpdateLava(PlayerHealth.LavaTickSeconds, inLava: true, immune: false);
        Assert.Equal(DamageSource.Lava, health.LastSource);
        Assert.Null(health.LastKiller);
    }
}
