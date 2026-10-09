using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// A connected player's survival status is the server's and each player's own: a mob hit lands on
/// the player it touched and queues that player's knockback, lava burns only the body standing in
/// it, a fall seen through accepted states is billed by the singleplayer curve unless the player
/// flies or its surroundings are not loaded, and a dead player stays dead and immune until it
/// respawns at its respawn eye with the fall forgotten.
/// </summary>
public sealed class ServerPlayerStatusTests
{
    public ServerPlayerStatusTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 20f;

    // Eye of a player standing on the y = 0 floor.
    private const float StandingEyeY = 1f + Camera.EyeHeight;

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    private static Vector3 EyeAtFeet(float x, float feet, float z) => new(x, feet + Camera.EyeHeight, z);

    /// <summary>
    /// Drops <paramref name="player"/> from feet <paramref name="fromFeet"/> to the floor one block
    /// per accepted state at the client's state cadence, the way its states would report the fall.
    /// </summary>
    private static void FallTo(ServerPlayer player, ChunkManager chunks, float x, float z, float fromFeet)
    {
        player.Teleport(EyeAtFeet(x, fromFeet, z));
        for (var feet = fromFeet - 1f; feet >= 1f; feet -= 1f)
        {
            player.Position = EyeAtFeet(x, feet, z);
            player.ObserveAcceptedState(chunks, WorldGenerationConfig.NetPlayerStateSeconds);
        }
    }

    [Fact]
    public void AMobHitDamagesOnlyTheTargetItLandedOn()
    {
        var chunks = StoneFloor();
        var drops = new DropManager();
        var manager = new SlimeManager(EntityRegistry.Require("slime"));

        var hit = new ServerPlayer(1, "Hit", new Vector3(12.5f, StandingEyeY, 8.5f));
        var bystander = new ServerPlayer(2, "Bystander", new Vector3(2.5f, StandingEyeY, 8.5f));

        // An airborne slime in the hit player's body, a little toward the bystander's side
        var slimeAt = new Vector3(hit.Position.X - 0.2f, 2f, hit.Position.Z);
        var slime = manager.Spawn(slimeAt);
        Assert.True(slime.CanDealContactDamage);
        Assert.True(MobTargets.BodyTouches(hit.Position, slime.Position, Slime.HalfExtent));
        Assert.False(MobTargets.BodyTouches(bystander.Position, slime.Position, Slime.HalfExtent));

        manager.Update(1f / 60f, chunks, drops, [bystander, hit]);

        Assert.True(manager.DamageDealtThisFrame > 0);
        Assert.Equal(PlayerHealth.MaxHealth - manager.DamageDealtThisFrame, hit.Health);
        Assert.True(hit.StatusDirty);
        Assert.True(hit.TakePendingKnockback(out var impulse, out _));
        // The shove pushes the player away from the slime that hit it
        Assert.True(Vector3.Dot(impulse, hit.Position - slimeAt) > 0f);
        // Handing the knockback over clears it
        Assert.False(hit.TakePendingKnockback(out _, out _));

        Assert.Equal(PlayerHealth.MaxHealth, bystander.Health);
        Assert.False(bystander.StatusDirty);
        Assert.False(bystander.TakePendingKnockback(out _, out _));
    }

    [Fact]
    public void LavaBurnsThePlayerStandingInItAndNobodyElse()
    {
        var chunks = StoneFloor();
        var lava = ContentRegistry.RequireBlock("lava").Id;
        var chunk = chunks.Chunks[Vector3.Zero];
        chunk.SetBlockRaw(3, 1, 3, lava, 0);
        chunk.SetBlockRaw(6, 1, 6, lava, 0);

        var burning = new ServerPlayer(1, "Burning", new Vector3(3.5f, StandingEyeY, 3.5f));
        var dry = new ServerPlayer(2, "Dry", new Vector3(12.5f, StandingEyeY, 12.5f));
        var creative = new ServerPlayer(3, "Creative", new Vector3(6.5f, StandingEyeY, 6.5f)) { Creative = true };

        var seconds = 3f * PlayerHealth.LavaTickSeconds;
        for (var t = 0f; t < seconds; t += Dt)
        {
            burning.TickStatus(Dt, chunks);
            dry.TickStatus(Dt, chunks);
            creative.TickStatus(Dt, chunks);
        }

        Assert.True(burning.Health < PlayerHealth.MaxHealth);
        Assert.True(burning.StatusDirty);
        Assert.Equal(PlayerHealth.MaxHealth, dry.Health);
        Assert.False(dry.StatusDirty);
        // Creative is immune to the lava it stands in
        Assert.Equal(PlayerHealth.MaxHealth, creative.Health);
    }

    [Fact]
    public void AFallFromAcceptedStatesHurtsUnlessFlyingOrUnknown()
    {
        var chunks = StoneFloor();
        const float drop = 8f;
        var expected = PlayerHealth.FallDamage(drop);
        Assert.True(expected > 0);

        var faller = new ServerPlayer(1, "Faller", Vector3.Zero);
        FallTo(faller, chunks, 8.5f, 8.5f, 1f + drop);
        Assert.Equal(PlayerHealth.MaxHealth - expected, faller.Health);
        Assert.True(faller.StatusDirty);

        var flyer = new ServerPlayer(2, "Flyer", Vector3.Zero) { Creative = true, Flying = true };
        FallTo(flyer, chunks, 8.5f, 8.5f, 1f + drop);
        Assert.Equal(PlayerHealth.MaxHealth, flyer.Health);

        // The same fall over a column whose chunk is not loaded is never judged
        var unknown = new ServerPlayer(3, "Unknown", Vector3.Zero);
        var farX = 3f * Chunk.Size + 0.5f;
        Assert.False(PlayerBodyProbe.IsKnown(EyeAtFeet(farX, 1f, 8.5f), chunks));
        FallTo(unknown, chunks, farX, 8.5f, 1f + drop);
        Assert.Equal(PlayerHealth.MaxHealth, unknown.Health);
        Assert.False(unknown.StatusDirty);
    }

    [Fact]
    public void ADeadPlayerStaysDeadUntilRespawnWhichReturnsToTheRespawnEye()
    {
        var chunks = StoneFloor();
        var lava = ContentRegistry.RequireBlock("lava").Id;
        chunks.Chunks[Vector3.Zero].SetBlockRaw(3, 1, 3, lava, 0);

        var home = new Vector3(12.5f, StandingEyeY, 12.5f);
        var player = new ServerPlayer(1, "Player", Vector3.Zero) { RespawnEye = home };

        // Airborne high enough that landing from here would hurt, then killed in mid-air
        const float height = 7f;
        Assert.True(PlayerHealth.FallDamage(height) > 0);
        player.Teleport(EyeAtFeet(3.5f, 1f + height, 3.5f));
        player.ObserveAcceptedState(chunks, WorldGenerationConfig.NetPlayerStateSeconds);
        player.Status.TakeDamage(PlayerHealth.MaxHealth);
        Assert.True(player.IsDead);
        Assert.True(player.IsImmune);

        // Nothing brings it back on its own: the status ticks (even in lava) and states change nothing
        player.Position = EyeAtFeet(3.5f, 1f, 3.5f);
        for (var t = 0f; t < 3f; t += Dt)
        {
            player.TickStatus(Dt, chunks);
            player.ObserveAcceptedState(chunks, Dt);
        }
        Assert.True(player.IsDead);
        Assert.Equal(0, player.Health);

        player.StatusDirty = false;
        player.Respawn(fallbackEye: new Vector3(4.5f, StandingEyeY, 12.5f));
        Assert.False(player.IsDead);
        Assert.False(player.IsImmune);
        Assert.Equal(PlayerHealth.MaxHealth, player.Health);
        Assert.Equal(home, player.Position);
        Assert.True(player.StatusDirty);

        // The respawn forgot the fall it died in: standing at home lands free
        player.ObserveAcceptedState(chunks, WorldGenerationConfig.NetPlayerStateSeconds);
        Assert.Equal(PlayerHealth.MaxHealth, player.Health);

        // With no respawn eye of its own, a player comes back at the fallback
        var fallback = new Vector3(4.5f, StandingEyeY, 12.5f);
        var homeless = new ServerPlayer(2, "Homeless", Vector3.Zero);
        homeless.Status.TakeDamage(PlayerHealth.MaxHealth);
        homeless.Respawn(fallback);
        Assert.Equal(fallback, homeless.Position);
        Assert.Equal(PlayerHealth.MaxHealth, homeless.Health);
    }
}
