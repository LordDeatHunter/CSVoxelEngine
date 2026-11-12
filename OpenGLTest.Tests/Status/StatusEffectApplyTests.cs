using System.Numerics;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.Status;
using Xunit;

namespace VoxelEngine.Tests.Status;

/// <summary>
/// The status effect API (<see cref="StatusEffects.Apply(Mob, string, float)"/> and its overloads) and
/// where effects tick in the authoritative simulation: an immune, dead or effect-less target refuses an
/// effect, a server player's status turns dirty when its effects change and burns only for the effect's
/// duration, and a mob's hazard tick runs its damage over time, a lethal one killing it like lava.
/// </summary>
public sealed class StatusEffectApplyTests
{
    public StatusEffectApplyTests() => TestContent.EnsureLoaded();

    // Binary-exact, so tick counts never drift
    private const float Dt = 1f / 16f;

    private static readonly IMobTarget[] Nobody = [];

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    private static ServerPlayer StandingPlayer() =>
        new(1, "Player", new Vector3(8.5f, 1f + Camera.EyeHeight, 8.5f));

    [Fact]
    public void AnImmuneOrDeadServerPlayerRefusesAnEffect()
    {
        var burning = StatusEffectRegistry.Require("burning");

        var creative = StandingPlayer();
        creative.Creative = true;
        Assert.False(StatusEffects.Apply(creative, "burning"));
        Assert.False(creative.Effects.IsActive(burning));

        var flying = StandingPlayer();
        flying.Creative = true;
        flying.Flying = true;
        Assert.False(StatusEffects.Apply(flying, "burning"));

        var dead = StandingPlayer();
        dead.Status.TakeDamage(PlayerHealth.MaxHealth);
        Assert.True(dead.IsDead);
        Assert.False(StatusEffects.Apply(dead, "burning"));

        var survival = StandingPlayer();
        Assert.True(StatusEffects.Apply(survival, "burning"));
        Assert.True(survival.Effects.IsActive(burning));

        // The pool overload follows the same rule
        var pool = new PlayerHealth();
        Assert.False(StatusEffects.Apply(pool, immune: true, "burning"));
        Assert.True(StatusEffects.Apply(pool, immune: false, "burning"));
        Assert.True(pool.Effects.IsActive(burning));
    }

    [Fact]
    public void ACameraWithNoEffectSetRefusesAnEffect()
    {
        var camera = new Camera(null, null);
        Assert.Null(camera.Effects);
        Assert.False(StatusEffects.Apply(camera, "burning"));
    }

    [Fact]
    public void ApplyingAndExpiringMarksTheServerStatusDirty()
    {
        var chunks = StoneFloor();
        var player = StandingPlayer();
        // A slow that deals no damage, so only the change of the active set can dirty the status
        var slow = StatusEffectLoader.Compile("slow", new StatusEffectDefinition { DurationSeconds = 1f, MoveFactor = 0.5f },
            "slow.json", 0);

        player.TickStatus(Dt, chunks);
        player.StatusDirty = false;

        Assert.True(player.Effects.Apply(slow, 1f));
        player.TickStatus(Dt, chunks);
        Assert.True(player.StatusDirty);

        // Nothing changes while it lasts
        player.StatusDirty = false;
        player.TickStatus(Dt, chunks);
        Assert.False(player.StatusDirty);

        var expired = false;
        for (var t = 0f; t < 2f && !expired; t += Dt)
        {
            player.StatusDirty = false;
            player.TickStatus(Dt, chunks);
            expired = !player.Effects.IsActive(slow);
            Assert.Equal(expired, player.StatusDirty);
        }
        Assert.True(expired, "the effect never expired");
    }

    [Fact]
    public void AServerPlayerBurnsOnlyForTheEffectsDuration()
    {
        var chunks = StoneFloor();
        var player = StandingPlayer();
        var burning = StatusEffectRegistry.Require("burning");
        Assert.True(StatusEffects.Apply(player, "burning"));

        for (var t = 0f; t < burning.DurationSeconds + Dt; t += Dt)
            player.TickStatus(Dt, chunks);

        Assert.False(player.Effects.IsActive(burning));
        var afterBurn = player.Health;
        // At least one tick fit the duration and landed
        Assert.True(burning.TickSeconds <= burning.DurationSeconds);
        Assert.True(afterBurn < PlayerHealth.MaxHealth, "the burn never hurt");
        Assert.Equal(DamageSource.Magic, player.Status.LastSource);

        // Past its duration the effect costs nothing more (regeneration may only raise the pool)
        var lowest = afterBurn;
        for (var t = 0f; t < burning.TickSeconds * 3f; t += Dt)
        {
            player.TickStatus(Dt, chunks);
            Assert.True(player.Health >= lowest, "the player kept burning after the effect ended");
            lowest = player.Health;
        }
    }

    [Fact]
    public void AMobDiesOfAnEffectThroughItsHazardTick()
    {
        var chunks = StoneFloor();
        var drops = new DropManager();
        var manager = new SlimeManager(EntityRegistry.Require("slime"));
        var slime = manager.Spawn(new Vector3(8.5f, 1.05f + Slime.HalfExtent, 8.5f))!;
        var lethal = StatusEffectLoader.Compile("lethal", new StatusEffectDefinition
        {
            DurationSeconds = 5f, TickSeconds = 0.25f, DamageShare = 1f,
        }, "lethal.json", 0);
        Assert.True(slime.Effects.Apply(lethal, 1f));

        for (var t = 0f; t < 2f && !slime.IsDying; t += Dt)
            manager.Update(Dt, chunks, drops, Nobody);

        Assert.True(slime.IsDying, "the effect never killed the slime");
        Assert.NotEmpty(drops.Drops);

        // A dying mob's effects are cleared on its next hazard tick
        slime.UpdateHazards(Dt, chunks);
        Assert.Equal(0, slime.Effects.Mask);
    }

    [Fact]
    public void ADyingMobRefusesAnEffect()
    {
        var slime = new Slime(EntityRegistry.Require("slime"), new Vector3(8.5f, 1.05f + Slime.HalfExtent, 8.5f));
        Assert.True(StatusEffects.Apply(slime, "burning"));

        var dying = new Slime(EntityRegistry.Require("slime"), new Vector3(8.5f, 1.05f + Slime.HalfExtent, 8.5f));
        dying.BeginDying();
        Assert.False(StatusEffects.Apply(dying, "burning"));
        Assert.Equal(0, dying.Effects.Mask);
    }
}
