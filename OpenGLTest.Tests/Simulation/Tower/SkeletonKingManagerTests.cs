using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Tower;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Tower;

/// <summary>
/// The Skeleton King's manager (<see cref="SkeletonKingManager"/>): one king per tower and none past the hard cap, a
/// kill reads as killed before the next update while a king that is left alone (everyone far off, or nobody in play)
/// fades out without loot and never reads as killed, a summon raises skeletons and never more
/// living than its minion cap, a volley fans several bones, the slam's shockwave reaches only nearby targets, and the
/// slam throws bone shrapnel that flies through the king and its skeletons and hurts only the target it touches.
/// </summary>
public sealed class SkeletonKingManagerTests
{
    public SkeletonKingManagerTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;
    private const float Leash = 20f;

    private static readonly (int, int) TowerA = (3, -2);
    private static readonly (int, int) TowerB = (7, 5);

    private static EntityType KingType => EntityRegistry.Require(SkeletonKing.EntityName);
    private static EntityType SkeletonType => EntityRegistry.Require(Skeleton.EntityName);

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    // Feet and body center of a king standing on the y = 0 floor
    private static Vector3 FeetAt(float x, float z) => new(x, 1f, z);
    private static Vector3 CenterAt(Vector3 feet) => feet + new Vector3(0f, MobConfig.SkeletonKingHeight * 0.5f, 0f);

    private static SkeletonKingHome HomeAt(Vector3 feet, (int, int)? tower) => new(tower, feet, Leash);

    private static SkeletonKing SpawnBound(SkeletonKingManager manager, Vector3 feet, (int, int) tower) =>
        manager.Spawn(CenterAt(feet), HomeAt(feet, tower), spawnIn: false)!;

    private static StubTarget PlayerAt(float x, float z, float feetY = 1f) =>
        new(StubTarget.EyeAt(new Vector3(x, feetY, z)));

    [Fact]
    public void OneKingAlivePerTower()
    {
        var manager = new SkeletonKingManager(KingType);
        var feet = FeetAt(8.5f, 8.5f);

        var first = SpawnBound(manager, feet, TowerA);
        Assert.NotNull(first);
        Assert.Null(manager.Spawn(CenterAt(feet), HomeAt(feet, TowerA)));
        Assert.NotNull(manager.Spawn(CenterAt(feet), HomeAt(feet, TowerB)));

        // Killed still holds the tower: no second king rises over a fallen one
        first.TakeDamage(first.Health);
        Assert.Null(manager.Spawn(CenterAt(feet), HomeAt(feet, TowerA)));

        // Unbound kings never claim a tower, so several may stand
        Assert.NotNull(manager.Spawn(CenterAt(feet), SkeletonKingHome.Unbound(feet)));
        Assert.NotNull(manager.Spawn(CenterAt(feet), SkeletonKingHome.Unbound(feet)));

        // Without registered content nothing spawns
        Assert.Null(new SkeletonKingManager(null).Spawn(CenterAt(feet), HomeAt(feet, TowerA)));
    }

    [Fact]
    public void ASpawnStopsAtTheHardCap()
    {
        var caps = new MobCaps();
        var manager = new SkeletonKingManager(KingType, caps);
        var feet = FeetAt(8.5f, 8.5f);
        while (caps.ForcedRoom > 0)
            Assert.NotNull(manager.Spawn(CenterAt(feet), SkeletonKingHome.Unbound(feet)));

        Assert.Null(manager.Spawn(CenterAt(feet), HomeAt(feet, TowerA)));
        Assert.Equal(KingStatus.None, manager.StatusFor(TowerA));
    }

    [Fact]
    public void AKillReadsAsKilledBeforeTheNextUpdate()
    {
        var chunks = StoneFloor();
        var manager = new SkeletonKingManager(KingType);
        var drops = new DropManager();
        var feet = FeetAt(8.5f, 8.5f);
        var king = SpawnBound(manager, feet, TowerA);
        // A living unbound king beside it never lends the tower its status
        manager.Spawn(CenterAt(feet), SkeletonKingHome.Unbound(feet), spawnIn: false);
        Assert.Equal(KingStatus.Alive, manager.StatusFor(TowerA));
        Assert.Equal(KingStatus.None, manager.StatusFor(TowerB));

        king.TakeDamage(king.Health);
        Assert.False(king.IsDying);
        Assert.Equal(KingStatus.Killed, manager.StatusFor(TowerA));

        manager.Update(Dt, chunks, drops, [PlayerAt(10.5f, 8.5f)]);
        Assert.True(king.IsDying);
        Assert.Equal(KingStatus.Killed, manager.StatusFor(TowerA));
        Assert.NotEmpty(drops.Drops);
        Assert.Equal(KingStatus.None, manager.StatusFor(TowerB));
    }

    [Fact]
    public void LeavingFadesTheKingWithoutADefeat()
    {
        var chunks = StoneFloor();
        var manager = new SkeletonKingManager(KingType);
        var drops = new DropManager();
        var king = SpawnBound(manager, FeetAt(8.5f, 8.5f), TowerA);
        IMobTarget[] far = [PlayerAt(8.5f + MobConfig.SkeletonKingLeaveRadiusBlocks * 2f, 8.5f)];

        manager.Update(Dt, chunks, drops, far);
        Assert.True(king.IsDying);
        Assert.True(king.Health > 0);
        Assert.Equal(KingStatus.Fading, manager.StatusFor(TowerA));
        Assert.Empty(drops.Drops);

        // Once it has faded the tower is free for a new king
        for (var i = 0; i < (int)(10f / Dt) && manager.Mobs.Count > 0; i++)
        {
            manager.Update(Dt, chunks, drops, far);
            Assert.NotEqual(KingStatus.Killed, manager.StatusFor(TowerA));
        }
        Assert.Equal(KingStatus.None, manager.StatusFor(TowerA));
        Assert.NotNull(SpawnBound(manager, FeetAt(8.5f, 8.5f), TowerA));
    }

    [Fact]
    public void AnEmptyServerFadesABoundKing()
    {
        var chunks = StoneFloor();
        var manager = new SkeletonKingManager(KingType);
        var drops = new DropManager();
        var king = SpawnBound(manager, FeetAt(8.5f, 8.5f), TowerA);

        manager.Update(Dt, chunks, drops, []);
        Assert.True(king.IsDying);
        Assert.Equal(KingStatus.Fading, manager.StatusFor(TowerA));

        for (var i = 0; i < (int)(10f / Dt) && manager.Mobs.Count > 0; i++)
        {
            manager.Update(Dt, chunks, drops, []);
            Assert.NotEqual(KingStatus.Killed, manager.StatusFor(TowerA));
        }
        Assert.Empty(drops.Drops);
        Assert.Equal(KingStatus.None, manager.StatusFor(TowerA));
    }

    [Fact]
    public void ASummonRaisesSkeletonsUnderTheMinionCap()
    {
        var chunks = StoneFloor();
        var caps = new MobCaps();
        var manager = new SkeletonKingManager(KingType, caps);
        var minions = new SkeletonManager(SkeletonType, caps);
        var drops = new DropManager();
        var king = SpawnBound(manager, FeetAt(4.5f, 8.5f), TowerA);
        // Out of slam reach, so the king's first decisions only walk
        IMobTarget[] targets = [PlayerAt(10.5f, 8.5f)];

        manager.Update(Dt, chunks, drops, targets, minions: minions);
        Assert.Equal(1, king.Phase);

        // Crossing into phase 2 owes a summon, which the king calls at once
        king.TakeDamage(king.Health - king.MaxHealth / 2);
        for (var i = 0; i < (int)(4f / Dt) && minions.LivingCount == 0; i++)
            manager.Update(Dt, chunks, drops, targets, minions: minions);

        Assert.InRange(minions.LivingCount, 1, MobConfig.SkeletonKingSummonCount);
        Assert.Equal(minions.LivingCount, manager.LivingMinions);
        foreach (var skeleton in minions.Mobs)
        {
            Assert.Null(skeleton.Floor);
        }

        // Wave after wave never raises more living minions than the cap
        for (var wave = 0; wave < MobConfig.SkeletonKingMaxMinions; wave++)
        {
            manager.TestSummon(king, minions, chunks);
            Assert.True(manager.LivingMinions <= MobConfig.SkeletonKingMaxMinions);
            Assert.True(minions.LivingCount <= MobConfig.SkeletonKingMaxMinions);
        }
        Assert.Equal(MobConfig.SkeletonKingMaxMinions, manager.LivingMinions);

        // A fallen minion frees its place for the next wave
        var fallen = minions.Mobs[0];
        fallen.TakeDamage(fallen.Health);
        manager.TestSummon(king, minions, chunks);
        Assert.Equal(MobConfig.SkeletonKingMaxMinions, manager.LivingMinions);
    }

    [Fact]
    public void AKingOnTheAltarRaisesItsSkeletonsOnTheRoofBelow()
    {
        var chunks = StoneFloor();
        // The altar: one block on the floor, the king standing on it a cell above where its skeletons stand
        chunks.Chunks[Vector3.Zero].SetBlockRaw(8, 1, 8, ContentRegistry.RequireBlock("stone").Id, 0);
        var caps = new MobCaps();
        var manager = new SkeletonKingManager(KingType, caps);
        var minions = new SkeletonManager(SkeletonType, caps);
        var king = SpawnBound(manager, new Vector3(8.5f, 2f, 8.5f), TowerA);

        manager.TestSummon(king, minions, chunks);

        Assert.NotEmpty(minions.Mobs);
        foreach (var skeleton in minions.Mobs)
            Assert.Equal(1f, skeleton.Feet.Y, 3);
    }

    [Fact]
    public void AVolleyBurstsItsBonesOneAfterAnotherEachOnItsOwnHeading()
    {
        var chunks = StoneFloor();
        var manager = new SkeletonKingManager(KingType);
        var drops = new DropManager();
        SpawnBound(manager, FeetAt(1.5f, 8.5f), TowerA);
        IMobTarget[] targets = [PlayerAt(13.5f, 8.5f)];

        for (var i = 0; i < (int)(6f / Dt) && manager.Bones.Count == 0; i++)
            manager.Update(Dt, chunks, drops, targets);
        // The first bone leaves at the release, the rest over the burst's window, not all in one frame
        Assert.True(manager.Bones.Count < MobConfig.SkeletonKingBurstBones, "the whole burst left at once");
        for (var i = 0; i < (int)(MobConfig.SkeletonKingBurstSeconds / Dt) + 2; i++)
            manager.Update(Dt, chunks, drops, targets);

        var bones = manager.Bones;
        Assert.True(bones.Count > 1, "the burst threw fewer than two bones");
        var headings = new List<float>();
        foreach (var bone in bones)
            headings.Add(MathF.Atan2(bone.Velocity.Z, bone.Velocity.X));
        for (var a = 0; a < headings.Count; a++)
        for (var b = a + 1; b < headings.Count; b++)
            Assert.True(MathF.Abs(headings[a] - headings[b]) > 1e-4f, "two bones flew the same heading");
    }

    [Fact]
    public void TheSlamReachesOnlyInsideItsShockwave()
    {
        var chunks = StoneFloor();
        var manager = new SkeletonKingManager(KingType);
        var drops = new DropManager();
        SpawnBound(manager, FeetAt(8.5f, 8.5f), TowerA);

        // The chased target stands in slam reach; one stands past the shockwave's radius, one inside it but far above
        var close = PlayerAt(8.5f + MobConfig.SkeletonKingSlamTriggerReach * 0.5f, 8.5f);
        var outside = PlayerAt(8.5f - (MobConfig.SkeletonKingSlamRadius + 1f), 8.5f);
        var above = PlayerAt(8.5f, 8.5f + MobConfig.SkeletonKingSlamRadius * 0.75f,
            feetY: 1f + MobConfig.SkeletonKingSlamHeight + 3f);
        IMobTarget[] targets = [close, outside, above];

        for (var i = 0; i < (int)(4f / Dt) && close.DamageTaken == 0; i++)
            manager.Update(Dt, chunks, drops, targets);

        Assert.True(close.DamageTaken > 0, "the slam never landed on the target in reach");
        Assert.Equal(0, outside.DamageTaken);
        Assert.Equal(0, above.DamageTaken);
    }

    [Fact]
    public void ASlamThrowsBoneShrapnel()
    {
        var chunks = StoneFloor();
        var manager = new SkeletonKingManager(KingType);
        var drops = new DropManager();
        SpawnBound(manager, FeetAt(8.5f, 8.5f), TowerA);
        // In slam reach and inside the volley's minimum range, so every bone in flight is the slam's
        var close = PlayerAt(8.5f + MobConfig.SkeletonKingSlamTriggerReach * 0.5f, 8.5f);

        for (var i = 0; i < (int)(4f / Dt) && close.DamageTaken == 0; i++)
            manager.Update(Dt, chunks, drops, [close]);

        Assert.True(close.DamageTaken > 0, "the slam never landed");
        Assert.NotEmpty(manager.Bones);
    }

    [Fact]
    public void ASlamShardFliesThroughTheKingAndASkeletonAndHurtsOnlyTheTarget()
    {
        var chunks = StoneFloor();
        var caps = new MobCaps();
        var manager = new SkeletonKingManager(KingType, caps);
        var minions = new SkeletonManager(SkeletonType, caps);
        var drops = new DropManager();
        // The king, a skeleton and the target in a line, out of slam reach and before the king's first volley
        var king = SpawnBound(manager, FeetAt(2.5f, 8.5f), TowerA);
        var skeleton = minions.Spawn(new Vector3(6.5f, 1f + MobConfig.SkeletonHeight * 0.5f, 8.5f),
            spawnIn: false)!;
        var target = PlayerAt(10.5f, 8.5f);

        // Launched from inside the king, level at the target's chest, so it crosses the skeleton's body on the way
        var chest = RealmMob.ChestOf(target.Eye);
        manager.TestLaunchShard(new Vector3(king.Position.X, chest.Y, 8.5f), new Vector3(20f, 0f, 0f));
        var crossed = false;
        for (var i = 0; i < 120 && manager.Bones.Count > 0; i++)
        {
            manager.Update(Dt, chunks, drops, [target], minions: minions);
            foreach (var bone in manager.Bones)
                crossed |= skeleton.OverlapsBox(bone.Position, ThrownProjectile.HalfExtent);
        }

        Assert.True(crossed, "the shard never crossed the skeleton");
        Assert.Equal(king.MaxHealth, king.Health);
        Assert.Equal(skeleton.MaxHealth, skeleton.Health);
        Assert.Equal(MobConfig.SkeletonKingSlamShrapnelDamage, target.DamageTaken);
    }
}
