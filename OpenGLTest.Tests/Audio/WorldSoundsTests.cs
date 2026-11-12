using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Entities;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Interaction;
using Xunit;

namespace VoxelEngine.Tests.Audio;

/// <summary>
/// Where a world's sounds go (<see cref="WorldSounds"/>): a server queues every positional sound, with the player its
/// own client already played it for, up to the per-broadcast cap, and plays none of its own player's sounds; any other
/// world queues nothing. A mob struck on a server queues its hit sound, then its death sound, at the mob.
/// </summary>
public sealed class WorldSoundsTests
{
    public WorldSoundsTests() => TestContent.EnsureLoaded();

    [Fact]
    public void AServerQueuesPositionalSoundsWithTheirExceptedPlayer()
    {
        var sounds = new WorldSounds(null, serverMode: true);
        var at = new Vector3(3f, 64f, -7f);
        sounds.PlayAt(SfxLibrary.SlimeJumpGroup, at);
        sounds.PlayAt(SfxLibrary.StoneGroup, at, exceptPlayerId: 4);
        sounds.Play(SfxLibrary.SwooshGroup);

        Assert.Equal([new SoundEvent(SfxLibrary.SlimeJumpGroup, at), new SoundEvent(SfxLibrary.StoneGroup, at, 4)],
            sounds.Pending);

        sounds.ClearPending();
        Assert.Empty(sounds.Pending);
    }

    [Fact]
    public void AServerHoldsAtMostOneBroadcastOfSounds()
    {
        var sounds = new WorldSounds(null, serverMode: true);
        for (var i = 0; i < NetworkConfig.NetSoundEventsPerBroadcast * 2; i++)
            sounds.PlayAt(SfxLibrary.SlimeJumpGroup, Vector3.Zero);
        Assert.Equal(NetworkConfig.NetSoundEventsPerBroadcast, sounds.Pending.Count);
    }

    [Fact]
    public void AWorldThatPlaysItsOwnSoundsQueuesNothing()
    {
        var sounds = new WorldSounds(null, serverMode: false);
        sounds.PlayAt(SfxLibrary.SlimeJumpGroup, Vector3.Zero);
        Assert.Empty(sounds.Pending);
    }

    [Fact]
    public void AMobStruckOnAServerQueuesItsHitThenItsDeathAtTheMob()
    {
        var sounds = new WorldSounds(null, serverMode: true);
        var strikes = new MobStrikes(null!, null!, sounds, serverMode: true);
        var skeleton = new Skeleton(EntityRegistry.Require(Skeleton.EntityName), new Vector3(8.5f, 2f, 8.5f),
            spawnIn: false);

        var hits = skeleton.HitsTaken;
        skeleton.TakeDamage(1);
        strikes.ShowLandedHit(skeleton, hits);
        Assert.Equal(new SoundEvent(skeleton.HitSoundGroup, skeleton.Position), Assert.Single(sounds.Pending));

        sounds.ClearPending();
        hits = skeleton.HitsTaken;
        skeleton.TakeDamage(skeleton.Health + 100);
        strikes.ShowLandedHit(skeleton, hits);
        Assert.Equal(new SoundEvent(skeleton.DeathSoundGroup, skeleton.Position), Assert.Single(sounds.Pending));
    }
}
