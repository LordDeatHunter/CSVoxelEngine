using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Lod;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.Simulation.LeafDecay;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Tower;

/// <summary>
/// The battle tower's floor locks (<see cref="TowerManager"/>): a door opens only once its floor's piles are depleted
/// and nothing of it lives, is owed or waits to be restored, and only after the floor below opened; an unloaded door
/// cell never holds back the loaded ones; a floor's piles sleep until the floor below is open; a stray, despawned or
/// idle bound skeleton is owed back (an idle one rising again at full health); the scenario clear and the look-at line. Every record is synthetic, built inside one
/// loaded chunk, so nothing depends on the world seed.
/// </summary>
[Collection("LOD column caps")]
public sealed class TowerManagerTests
{
    public TowerManagerTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static readonly (int X, int Y, int Z) Pile0 = (4, 1, 4);
    private static readonly (int X, int Y, int Z) Pile1 = (4, 7, 4);
    private static readonly (int X, int Y, int Z) Door0A = (12, 2, 12);
    private static readonly (int X, int Y, int Z) Door0B = (12, 3, 12);
    private static readonly (int X, int Y, int Z) Door1A = (12, 8, 12);
    private static readonly (int X, int Y, int Z) Door1B = (12, 9, 12);
    // Door cells of floor 0 in chunks that are not loaded, one listed before the loaded cells and one after
    private static readonly (int X, int Y, int Z) UnloadedBefore = (12, 2, 12 + Chunk.Size);
    private static readonly (int X, int Y, int Z) UnloadedAfter = (12, 3, -4);

    private const int CenterX = 8;
    private const int CenterZ = 8;

    private static EntityType Type => EntityRegistry.Require(Skeleton.EntityName);

    private sealed class Rig
    {
        public required ChunkManager Chunks;
        public required SkeletonManager Skeletons;
        public required TowerManager Towers;
        public required TowerRecord Record;
        public readonly DropManager Drops = new();
        public TowerFloorState Floor0 => Record.Floors[0];
        public TowerFloorState Floor1 => Record.Floors[1];

        public void Update(IReadOnlyList<IMobTarget> targets, float dt = Dt, int pending = 0) =>
            Towers.Update(dt, Chunks, Skeletons, targets, f => f == Floor0.Ref ? pending : 0, null);

        public byte At((int X, int Y, int Z) c) => Chunks.GetBlockGlobal(c.X, c.Y, c.Z);
    }

    [Fact]
    public void OpeningAChestAlarmsOnlyTheLivePilesOfItsAwakeFloorInRange()
    {
        var rig = Setup();

        Assert.Equal(1, rig.Towers.AlarmPilesNear((6, 1, 6)));
        Assert.True(rig.Floor0.Piles[0].Alarmed);
        Assert.False(rig.Floor1.Piles[0].Alarmed);

        rig.Floor0.Piles[0].Alarmed = false;
        rig.Floor0.Piles[0].WavesLeft = 0;
        Assert.Equal(0, rig.Towers.AlarmPilesNear((6, 1, 6)));
        Assert.False(rig.Floor0.Piles[0].Alarmed);

        // Floor 1 sleeps until door 0 opens, so a chest up there wakes nothing yet
        Assert.Equal(0, rig.Towers.AlarmPilesNear((6, 7, 6)));
        rig.Floor0.DoorOpen = true;
        Assert.Equal(1, rig.Towers.AlarmPilesNear((6, 7, 6)));
    }

    // Two floors in the origin chunk: floor 0 (band 0..6) and floor 1 (band 6..12), a pile and a two-cell door each
    private static Rig Setup()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var chunk = chunks.Chunks[Vector3.Zero];
        foreach (var (x, y, z) in new[] { Pile0, Pile1 })
            chunk.SetBlockRaw(x, y, z, TowerPalette.PileBlock, 0);
        foreach (var (x, y, z) in new[] { Door0A, Door0B, Door1A, Door1B })
            chunk.SetBlockRaw(x, y, z, TowerPalette.DoorBlock, 0);

        var writer = new TowerBlockWriter(chunks, new FluidManager(), new LeafDecayManager(), new FallingBlockManager(),
            new LodManager(null!), (_, _, _) => { }, null);
        var towers = new TowerManager(writer, (_, _) => null);
        var record = TowerRecord.Build((5, -3), CenterX, CenterZ, 12,
        [
            (0, 6, new[] { Pile0 }, new[] { UnloadedBefore, Door0A, Door0B, UnloadedAfter }),
            (6, 12, new[] { Pile1 }, new[] { Door1A, Door1B }),
        ], hasSite: false);
        towers.Track(record);
        return new Rig { Chunks = chunks, Skeletons = new SkeletonManager(Type), Towers = towers, Record = record };
    }

    private static StubTarget OnFloor0() => new(StubTarget.EyeAt(new Vector3(6.5f, 1f, 6.5f)));

    private static StubTarget OnFloor1() => new(StubTarget.EyeAt(new Vector3(6.5f, 7f, 6.5f)));

    private static void Deplete(TowerFloorState floor)
    {
        foreach (var pile in floor.Piles) pile.WavesLeft = 0;
    }

    private static Skeleton Bound(Rig rig, TowerFloorState floor, Vector3 at)
    {
        var skeleton = rig.Skeletons.Spawn(at, floor.Floor, spawnIn: false)!;
        skeleton.Floor = floor.Ref;
        return skeleton;
    }

    [Fact]
    public void ADoorOpensOnlyWhenItsPilesAreDepletedAndItsSkeletonsGone()
    {
        var rig = Setup();

        // The pile still has its waves
        for (var i = 0; i < 5; i++) rig.Update([]);
        Assert.False(rig.Floor0.DoorOpen);

        // A target raises every wave: the pile depletes, but its skeletons live
        IMobTarget[] near = [OnFloor0()];
        for (var i = 0; i < MobConfig.SkeletonPileWaves + 2; i++) rig.Update(near, MobConfig.SkeletonPileWaveSeconds);
        Assert.True(rig.Floor0.Piles[0].Depleted);
        Assert.NotEmpty(rig.Skeletons.Mobs);
        Assert.Equal(rig.Skeletons.Mobs.Count, rig.Floor0.Living);
        Assert.False(rig.Floor0.DoorOpen);
        Assert.Equal(TowerPalette.DoorBlock, rig.At(Door0A));

        // Every skeleton dead, but one owed back
        foreach (var skeleton in rig.Skeletons.Mobs) skeleton.TakeDamage(skeleton.Health);
        rig.Floor0.Owed = 1;
        rig.Update([]);
        Assert.False(rig.Floor0.DoorOpen);

        rig.Floor0.Owed = 0;
        rig.Update([]);
        Assert.True(rig.Floor0.DoorOpen);
        Assert.Equal(BlockType.Air, rig.At(Door0A));
        Assert.Equal(BlockType.Air, rig.At(Door0B));
        Assert.Equal(BlockType.Air, rig.At(Pile0));
        // The floor above is untouched
        Assert.Equal(TowerPalette.DoorBlock, rig.At(Door1A));
    }

    [Fact]
    public void APendingRestoredSkeletonKeepsTheDoorShut()
    {
        var rig = Setup();
        Deplete(rig.Floor0);

        for (var i = 0; i < 3; i++) rig.Update([], pending: 1);
        Assert.False(rig.Floor0.DoorOpen);
        Assert.Equal(1, rig.Floor0.Alive);

        rig.Update([], pending: 0);
        Assert.True(rig.Floor0.DoorOpen);
    }

    [Fact]
    public void AnUnloadedDoorCellNeverHoldsBackTheLoadedOnes()
    {
        var rig = Setup();
        Deplete(rig.Floor0);

        rig.Update([]);

        Assert.True(rig.Floor0.DoorOpen);
        Assert.Equal(BlockType.Air, rig.At(Door0A));
        Assert.Equal(BlockType.Air, rig.At(Door0B));
        // The unloaded cells wait for their chunks
        Assert.Equal(2, rig.Floor0.DoorRemaining.Count);
        Assert.Contains(UnloadedBefore, rig.Floor0.DoorRemaining);
        Assert.Contains(UnloadedAfter, rig.Floor0.DoorRemaining);
    }

    [Fact]
    public void AFloorStaysLockedAndAsleepUntilThePreviousDoorOpens()
    {
        // A cleared floor 1 stays shut while floor 0 is not cleared
        var locked = Setup();
        Deplete(locked.Floor1);
        for (var i = 0; i < 5; i++) locked.Update([]);
        Assert.False(locked.Floor0.DoorOpen);
        Assert.False(locked.Floor1.DoorOpen);
        Assert.Equal(TowerPalette.DoorBlock, locked.At(Door1A));

        // Floor 1's pile sleeps for a target standing beside it until door 0 opens
        var rig = Setup();
        IMobTarget[] onFloor1 = [OnFloor1()];
        for (var i = 0; i < 5; i++) rig.Update(onFloor1, MobConfig.SkeletonPileWaveSeconds);
        Assert.Empty(rig.Skeletons.Mobs);
        Assert.Equal(MobConfig.SkeletonPileWaves, rig.Floor1.Piles[0].WavesLeft);

        Deplete(rig.Floor0);
        rig.Update(onFloor1, MobConfig.SkeletonPileWaveSeconds);
        Assert.True(rig.Floor0.DoorOpen);
        Assert.NotEmpty(rig.Skeletons.Mobs);
        Assert.All(rig.Skeletons.Mobs, s => Assert.Equal(rig.Floor1.Ref, s.Floor));
        Assert.False(rig.Floor1.DoorOpen);
    }

    [Fact]
    public void AStrayOrDespawnedBoundSkeletonIsOwedAndKeepsTheDoorShut()
    {
        // Past the leash: released and owed in the same frame, so the door never opens behind it
        var stray = Setup();
        Deplete(stray.Floor0);
        var far = Bound(stray, stray.Floor0,
            new Vector3(CenterX + 0.5f, 2f, CenterZ + 0.5f + MobConfig.TowerSkeletonLeashBlocks + 4f));
        stray.Update([]);
        Assert.True(far.IsDying);
        Assert.Equal(1, stray.Floor0.Owed);
        Assert.False(stray.Floor0.DoorOpen);

        // Despawned by its manager for want of a near target: owed, then raised again by a trigger
        var rig = Setup();
        Deplete(rig.Floor0);
        var skeleton = Bound(rig, rig.Floor0, new Vector3(CenterX + 0.5f, 2f, CenterZ + 0.5f));
        var distant = new StubTarget(StubTarget.EyeAt(new Vector3(CenterX + 0.5f, 1f,
            CenterZ + MobConfig.SkeletonDespawnRadiusBlocks + 20f)));
        for (var i = 0; i < 5 && !skeleton.IsDying; i++)
            rig.Skeletons.Update(Dt, rig.Chunks, rig.Drops, [distant]);
        Assert.True(skeleton.IsDying);

        rig.Update([]);
        Assert.Equal(1, rig.Floor0.Owed);
        Assert.False(rig.Floor0.DoorOpen);

        rig.Update([OnFloor0()], MobConfig.SkeletonPileWaveSeconds);
        Assert.Equal(0, rig.Floor0.Owed);
        Assert.Contains(rig.Skeletons.Mobs, s => !s.IsDying && !s.IsDead && s.Floor == rig.Floor0.Ref);
        Assert.False(rig.Floor0.DoorOpen);
    }

    [Fact]
    public void AnIdleBoundSkeletonCrumblesBackToItsPileAndRisesAgainAtFullHealth()
    {
        var rig = Setup();
        Deplete(rig.Floor0);
        var skeleton = Bound(rig, rig.Floor0, new Vector3(CenterX + 0.5f, 2f, CenterZ + 0.5f));
        skeleton.TakeDamage(skeleton.MaxHealth / 2);

        // Nobody in sight for its idle time: it crumbles, and its floor owes it, so the door stays shut
        for (var t = 0f; t < MobConfig.SkeletonPileReturnIdleSeconds + 1f && !skeleton.IsDying; t += Dt)
            rig.Skeletons.Update(Dt, rig.Chunks, rig.Drops, []);
        Assert.True(skeleton.IsDying);
        rig.Update([]);
        Assert.Equal(1, rig.Floor0.Owed);
        Assert.Equal(1, rig.Floor0.Alive);
        Assert.False(rig.Floor0.DoorOpen);

        // The next target on the floor raises it again from the pile, its wounds gone
        rig.Update([OnFloor0()], MobConfig.SkeletonPileWaveSeconds);
        Assert.Equal(0, rig.Floor0.Owed);
        var risen = Assert.Single(rig.Skeletons.Mobs, s => !s.IsDying && !s.IsDead && s.Floor == rig.Floor0.Ref);
        Assert.Equal(risen.MaxHealth, risen.Health);
        Assert.False(rig.Floor0.DoorOpen);
    }

    [Fact]
    public void ClearFloorKillsOnlyThatFloorsSkeletons()
    {
        var rig = Setup();
        var at = new Vector3(CenterX + 0.5f, 2f, CenterZ + 0.5f);
        var mine = Bound(rig, rig.Floor0, at);
        var above = Bound(rig, rig.Floor1, at + Vector3.UnitY * 6f);
        var unbound = rig.Skeletons.Spawn(at, 0, spawnIn: false)!;
        rig.Floor0.Owed = 2;

        Assert.True(rig.Towers.ClearFloor(at, 0, rig.Skeletons.Mobs));

        Assert.True(mine.IsDead);
        Assert.False(above.IsDead);
        Assert.False(unbound.IsDead);
        Assert.True(rig.Floor0.Piles[0].Depleted);
        Assert.Equal(0, rig.Floor0.Owed);
        Assert.False(rig.Floor1.Piles[0].Depleted);

        Assert.False(rig.Towers.ClearFloor(at, rig.Record.Floors.Length, rig.Skeletons.Mobs));
        Assert.False(rig.Towers.ClearFloor(at + new Vector3(500f, 0f, 0f), 0, rig.Skeletons.Mobs));
        Assert.False(new TowerManager(null, (_, _) => null).ClearFloor(at, 0, rig.Skeletons.Mobs));
    }

    [Fact]
    public void TheLookAtLineNamesTheSealedFloor()
    {
        var rig = Setup();
        rig.Update([]);

        var door0 = rig.Towers.Detail(TowerPalette.DoorBlock, Door0A.X, Door0A.Y, Door0A.Z);
        Assert.NotNull(door0);
        Assert.Contains("Floor 1", door0);
        var door1 = rig.Towers.Detail(TowerPalette.DoorBlock, Door1A.X, Door1A.Y, Door1A.Z);
        Assert.NotNull(door1);
        Assert.Contains("Floor 2", door1);
        Assert.Contains("floor 1", door1);
        Assert.Contains("Floor 2", rig.Towers.Detail(TowerPalette.PileBlock, Pile1.X, Pile1.Y, Pile1.Z));

        Assert.Null(rig.Towers.Detail(ContentRegistry.RequireBlock("stone").Id, Pile0.X, Pile0.Y, Pile0.Z));
        Assert.Null(rig.Towers.Detail(TowerPalette.DoorBlock, Door0A.X + 500, Door0A.Y, Door0A.Z));
    }
}
