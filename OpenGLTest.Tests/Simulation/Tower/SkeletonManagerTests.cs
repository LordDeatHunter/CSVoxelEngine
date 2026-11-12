using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Tower;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Tower;

/// <summary>
/// The skeleton manager (<see cref="SkeletonManager"/>): a forced spawn passes the natural cap but never the hard
/// cap, skeletons never appear on their own, a rake lands on a target in reach and an
/// arrow on the target it touches, an immune target is spared both, and a bound skeleton that chased nobody for its
/// idle time crumbles back to its floor (reported once) while one in sight of a target never does.
/// </summary>
public sealed class SkeletonManagerTests
{
    public SkeletonManagerTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static EntityType Type => EntityRegistry.Require(Skeleton.EntityName);

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    // Body center of a skeleton standing on the y = 0 floor
    private static Vector3 OnFloor(float x, float z) => new(x, 1f + MobConfig.SkeletonHeight * 0.5f, z);

    private static StubTarget PlayerAt(float x, float z) => new(StubTarget.EyeAt(new Vector3(x, 1f, z)));

    [Fact]
    public void AForcedSpawnPassesTheNaturalCapButNotTheHardCap()
    {
        var caps = new MobCaps();
        var manager = new SkeletonManager(Type, caps);
        var at = OnFloor(8.5f, 8.5f);
        for (var i = 0; i < MobConfig.MobNaturalCap; i++)
            Assert.NotNull(manager.Spawn(at));
        Assert.False(caps.AllowsNaturalSpawn);

        while (caps.ForcedRoom > 0)
            Assert.NotNull(manager.Spawn(at));
        Assert.Null(manager.Spawn(at));
        Assert.Equal(MobConfig.MobHardCap, caps.Living);
    }

    [Fact]
    public void OrdinarySpawnsAreHalfArchersAndHalfWarriorsEachOfItsOwnKind()
    {
        var manager = new SkeletonManager(Type);
        var at = OnFloor(8.5f, 8.5f);
        var spawned = new List<Skeleton>();
        for (var i = 0; i < 6; i++)
            spawned.Add(manager.Spawn(at)!);

        Assert.Equal(3, spawned.FindAll(s => s.Role == SkeletonRole.Archer).Count);
        Assert.Equal(3, spawned.FindAll(s => s.Role == SkeletonRole.Warrior).Count);
        Assert.All(spawned, s => Assert.Equal(Skeleton.EntityNameOf(s.Role), s.Type.Name));
        Assert.All(spawned, s => Assert.Equal(s.Role, Skeleton.RoleOf(s.Type.Name)));
    }

    [Fact]
    public void WithoutRegisteredContentNothingSpawns()
    {
        Assert.Null(new SkeletonManager(null).Spawn(OnFloor(8.5f, 8.5f)));
    }

    [Fact]
    public void SkeletonsNeverSpawnOnTheirOwn()
    {
        var chunks = StoneFloor();
        var manager = new SkeletonManager(Type);
        var drops = new DropManager();
        IMobTarget[] targets = [PlayerAt(8.5f, 8.5f)];

        for (var i = 0; i < 600; i++)
            manager.Update(0.5f, chunks, drops, targets);
        Assert.Empty(manager.Mobs);
    }

    [Fact]
    public void AMeleeImpactBillsATargetInReachButSparesAnImmuneOne()
    {
        var chunks = StoneFloor();
        var drops = new DropManager();

        var manager = new SkeletonManager(Type);
        manager.Spawn(OnFloor(8.5f, 8.5f), spawnIn: false);
        var close = PlayerAt(8.5f + MobConfig.SkeletonMeleeReach * 0.5f, 8.5f);
        var billed = false;
        for (var i = 0; i < (int)(4f / Dt) && close.DamageTaken == 0; i++)
        {
            manager.Update(Dt, chunks, drops, [close]);
            billed |= manager.DamageDealtThisFrame > 0;
        }
        Assert.True(close.DamageTaken > 0, "the skeleton never raked a target in reach");
        Assert.True(billed, "the rake was not counted into the frame's damage");

        var spared = new SkeletonManager(Type);
        spared.Spawn(OnFloor(8.5f, 8.5f), spawnIn: false);
        var immune = new StubTarget(close.Eye) { IsFlying = true };
        for (var i = 0; i < (int)(4f / Dt); i++)
        {
            spared.Update(Dt, chunks, drops, [immune]);
            Assert.Equal(0, spared.DamageDealtThisFrame);
        }
        Assert.Equal(0, immune.DamageTaken);
    }

    [Fact]
    public void AnArrowThatTouchesATargetHurtsItButSparesAnImmuneOne()
    {
        var chunks = StoneFloor();
        var manager = new SkeletonManager(Type);
        var drops = new DropManager();

        var target = PlayerAt(10.5f, 8.5f);
        var chest = RealmMob.ChestOf(target.Eye);
        manager.TestLaunchArrow(chest - new Vector3(4f, 0f, 0f), new Vector3(20f, 3f, 0f), damage: 5);
        for (var i = 0; i < 60 && manager.Arrows.Count > 0; i++)
            manager.Update(Dt, chunks, drops, [target]);

        Assert.Empty(manager.Arrows);
        Assert.Equal(5, target.DamageTaken);

        var flier = new StubTarget(target.Eye) { IsFlying = true };
        manager.TestLaunchArrow(chest - new Vector3(4f, 0f, 0f), new Vector3(20f, 3f, 0f), damage: 5);
        for (var i = 0; i < 60 && manager.Arrows.Count > 0; i++)
            manager.Update(Dt, chunks, drops, [flier]);
        Assert.Empty(manager.Arrows);
        Assert.Equal(0, flier.DamageTaken);
    }

    [Fact]
    public void ASkeletonInItsBandShootsAnArrowThatReachesItsTarget()
    {
        var chunks = StoneFloor();
        var manager = new SkeletonManager(Type);
        var drops = new DropManager();
        var band = (MobConfig.SkeletonBowMinRange + MobConfig.SkeletonBowMaxRange) * 0.5f;
        manager.Spawn(OnFloor(1.5f, 8.5f), spawnIn: false);
        var target = PlayerAt(1.5f + band, 8.5f);

        var launched = false;
        for (var i = 0; i < (int)(8f / Dt) && target.DamageTaken == 0; i++)
        {
            manager.Update(Dt, chunks, drops, [target]);
            launched |= manager.Arrows.Count > 0;
        }
        Assert.True(launched, "no arrow left the bow");
        Assert.True(target.DamageTaken > 0, "no arrow reached the target");
    }

    [Fact]
    public void ABoundSkeletonThatDespawnsIsReportedAndAnUnboundOneIsNot()
    {
        var chunks = StoneFloor();
        var manager = new SkeletonManager(Type);
        var drops = new DropManager();
        var floor = new TowerFloorRef(3, -2, 1);
        var bound = manager.Spawn(OnFloor(4.5f, 8.5f), spawnIn: false)!;
        bound.Floor = floor;
        var unbound = manager.Spawn(OnFloor(12.5f, 8.5f), spawnIn: false)!;
        IMobTarget[] far = [PlayerAt(8.5f + MobConfig.SkeletonDespawnRadiusBlocks * 2f, 8.5f)];

        manager.Update(Dt, chunks, drops, far);
        Assert.True(bound.IsDying);
        Assert.True(unbound.IsDying);

        var released = new List<TowerFloorRef>();
        manager.TakeReleased(released);
        Assert.Equal([floor], released);

        // Taking clears the report
        released.Clear();
        manager.TakeReleased(released);
        Assert.Empty(released);
    }

    [Fact]
    public void ReleasingABoundSkeletonReportsItOnce()
    {
        var chunks = StoneFloor();
        var manager = new SkeletonManager(Type);
        var drops = new DropManager();
        var floor = new TowerFloorRef(0, 0, 2);
        var skeleton = manager.Spawn(OnFloor(8.5f, 8.5f), spawnIn: false)!;
        skeleton.Floor = floor;

        manager.Release(skeleton);
        Assert.True(skeleton.IsDying);
        manager.Release(skeleton);
        // Now far from every target too: a dying skeleton never despawns a second time
        manager.Update(Dt, chunks, drops, [PlayerAt(8.5f + MobConfig.SkeletonDespawnRadiusBlocks * 2f, 8.5f)]);

        var released = new List<TowerFloorRef>();
        manager.TakeReleased(released);
        Assert.Equal([floor], released);

        // An unbound skeleton released reports nothing
        var unbound = manager.Spawn(OnFloor(8.5f, 8.5f), spawnIn: false)!;
        manager.Release(unbound);
        released.Clear();
        manager.TakeReleased(released);
        Assert.Empty(released);
    }

    [Fact]
    public void ABoundSkeletonIdleLongEnoughCrumblesBackToItsPileButNeverWhileItSeesATarget()
    {
        var chunks = StoneFloor();
        var drops = new DropManager();
        var floor = new TowerFloorRef(3, -2, 1);

        // Nobody in sight: the bound one crumbles once its idle time is up, without loot; an unbound one has no pile
        var idle = new SkeletonManager(Type);
        var bound = idle.Spawn(OnFloor(4.5f, 8.5f), spawnIn: false)!;
        bound.Floor = floor;
        var unbound = idle.Spawn(OnFloor(12.5f, 8.5f), spawnIn: false)!;
        var elapsed = 0f;
        while (!bound.IsDying && elapsed < MobConfig.SkeletonPileReturnIdleSeconds * 2f)
        {
            idle.Update(Dt, chunks, drops, []);
            elapsed += Dt;
        }
        Assert.True(bound.IsDying, "an idle bound skeleton never crumbled");
        Assert.True(elapsed >= MobConfig.SkeletonPileReturnIdleSeconds - 0.1f, "it crumbled before its idle time was up");
        Assert.False(unbound.IsDying);
        Assert.Empty(drops.Drops);

        // Reported once, so its floor owes exactly one skeleton back
        for (var i = 0; i < 60; i++) idle.Update(Dt, chunks, drops, []);
        var released = new List<TowerFloorRef>();
        idle.TakeReleased(released);
        Assert.Equal([floor], released);

        // A target in sight keeps a bound skeleton standing however long the fight lasts
        var watched = new SkeletonManager(Type);
        var engaged = watched.Spawn(OnFloor(4.5f, 8.5f), spawnIn: false)!;
        engaged.Floor = floor;
        IMobTarget[] near = [PlayerAt(8.5f, 8.5f)];
        for (var t = 0f; t < MobConfig.SkeletonPileReturnIdleSeconds * 1.5f; t += Dt)
            watched.Update(Dt, chunks, drops, near);
        Assert.False(engaged.IsDying);
        released.Clear();
        watched.TakeReleased(released);
        Assert.Empty(released);
    }
}
