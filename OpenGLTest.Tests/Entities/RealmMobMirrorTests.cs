using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.CaveMobs;
using VoxelEngine.Entities.HellMobs;
using VoxelEngine.Entities.SkyMobs;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Entities.Wildlife;
using Xunit;

namespace VoxelEngine.Tests.Entities;

/// <summary>
/// The realm mobs' replicated attack: the simulating mob counts every attack it starts once, and a client's
/// mirror, which never simulates, plays its attack clip exactly when the server's count moves, then returns to
/// its locomotion; the first count it sees and a count that moves while it dies play nothing.
/// </summary>
public sealed class RealmMobMirrorTests
{
    private const float Dt = 1f / 60f;

    public RealmMobMirrorTests() => TestContent.EnsureLoaded();

    private static RealmMob Create(string kind, Vector3 position)
    {
        var type = EntityRegistry.Require(kind);
        return kind switch
        {
            Hellbrute.EntityName => new Hellbrute(type, position, spawnIn: false),
            Cinderwitch.EntityName => new Cinderwitch(type, position, spawnIn: false),
            Galeraptor.EntityName => new Galeraptor(type, position, spawnIn: false),
            Skeleton.EntityName => new Skeleton(type, position, tier: 0, spawnIn: false),
            Shardling.EntityName => new Shardling(type, position, tier: 0, spawnIn: false),
            Sporeling.EntityName => new Sporeling(type, position, tier: 0, spawnIn: false),
            Magmaborn.EntityName => new Magmaborn(type, position, tier: 0, spawnIn: false),
            Cloudgolem.EntityName => new Cloudgolem(type, position, spawnIn: false),
            _ => WildlifeSpecies.Create(type, position, spawnIn: false)!,
        };
    }

    private static void Animate(RealmMob mob, float seconds)
    {
        for (var i = 0; i < (int)(seconds / Dt); i++)
            mob.AnimateMirror(Dt);
    }

    [Theory]
    [InlineData(Hellbrute.EntityName)]
    [InlineData(Cinderwitch.EntityName)]
    [InlineData(Galeraptor.EntityName)]
    [InlineData(Cloudgolem.EntityName)]
    [InlineData(Skeleton.EntityName)]
    [InlineData(Shardling.EntityName)]
    [InlineData(Sporeling.EntityName)]
    [InlineData(Magmaborn.EntityName)]
    [InlineData(Deer.EntityName)]
    [InlineData(Boar.EntityName)]
    [InlineData(Boar.DireEntityName)]
    public void AMirrorPlaysItsAttackClipWhenTheServersCountMovesThenReturnsToLocomotion(string kind)
    {
        var mirror = Create(kind, new Vector3(0f, 80f, 0f));

        // First sighting mid-fight: the count only sets, so an attack already under way is not replayed
        mirror.MirrorAttackCount(5);
        Animate(mirror, 0.1f);
        Assert.False(mirror.Animation.IsActionPlaying);
        mirror.MirrorAttackCount(5);
        Assert.False(mirror.Animation.IsActionPlaying);

        // The server started an attack: the clip plays, and once it ends the locomotion layer has the body again
        mirror.MirrorAttackCount(6);
        Assert.True(mirror.Animation.IsPlaying(mirror.AttackClipFor(6)));
        Assert.Equal(6, mirror.AttackCount);
        Animate(mirror, 5f);
        Assert.False(mirror.Animation.IsActionPlaying);
        mirror.MirrorAttackCount(6);
        Assert.False(mirror.Animation.IsActionPlaying);

        // A dying mirror keeps its death even when the count moves
        mirror.BeginDying();
        Animate(mirror, 0.1f);
        mirror.MirrorAttackCount(7);
        Assert.False(mirror.Animation.IsPlaying(mirror.AttackClip));
    }

    [Fact]
    public void ASkeletonMirrorPlaysTheClipItsCountsRemainderNames()
    {
        var mirror = (Skeleton)Create(Skeleton.EntityName, new Vector3(0f, 80f, 0f));
        mirror.MirrorAttackCount(1);

        // An even count is a melee swing, an odd one a bow shot, whichever came before
        mirror.MirrorAttackCount(2);
        Assert.True(mirror.Animation.IsPlaying(Skeleton.MeleeClip));
        Assert.False(mirror.Animation.IsPlaying(Skeleton.ShootClip));
        Animate(mirror, 5f);

        mirror.MirrorAttackCount(3);
        Assert.True(mirror.Animation.IsPlaying(Skeleton.ShootClip));
        Assert.Equal(Skeleton.ShootClip, mirror.AttackClipFor(mirror.AttackCount));
        Animate(mirror, 5f);

        // Two shots in a row skip a count: the clip still follows the remainder
        mirror.MirrorAttackCount(5);
        Assert.True(mirror.Animation.IsPlaying(Skeleton.ShootClip));
    }

    [Fact]
    public void TheSimulatingMobCountsEachAttackItStartsOnce()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var brute = new Hellbrute(EntityRegistry.Require(Hellbrute.EntityName),
            new Vector3(6.5f, 1f + MobConfig.HellbruteHeight * 0.5f, 8.5f), spawnIn: false);
        var target = new StubTarget(StubTarget.EyeAt(new Vector3(8.5f, 1f, 8.5f)));

        var starts = 0;
        var wasSwinging = false;
        for (var i = 0; i < (int)(10f / Dt); i++)
        {
            brute.Update(Dt, chunks, target);
            var swinging = brute.Animation.IsPlaying(brute.AttackClip);
            if (swinging && !wasSwinging) starts++;
            wasSwinging = swinging;
            brute.TryTakeMeleeImpact();
        }

        Assert.True(starts >= 2, "the brute should swing more than once in ten seconds at a target in reach");
        Assert.Equal(starts, brute.AttackCount);
    }
}
