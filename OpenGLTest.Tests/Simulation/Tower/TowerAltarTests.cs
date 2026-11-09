using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Lod;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.Simulation.LeafDecay;
using VoxelEngine.Simulation.Tower;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Tower;

/// <summary>
/// The battle tower's roof altar (<see cref="TowerManager.JudgeAltar"/>, <see cref="TowerManager.BeginRitual"/>,
/// <see cref="TowerManager.MarkDefeated"/>, <see cref="TowerManager.UpdateAltars"/>, <see cref="TowerManager.SettleAltars"/>):
/// the ritual is refused while any floor is sealed, begins once and then only stirs, never begins again after the king
/// fell, never begins on a client's mirror, and answers only on the altar cell; once it runs out exactly one king rises,
/// a king that leaves unkilled frees the ritual unless its restore is pending, and a kill defeats the tower the same
/// frame. Every record is synthetic, so nothing depends on the world seed.
/// </summary>
[Collection("LOD column caps")]
public sealed class TowerAltarTests
{
    public TowerAltarTests() => TestContent.EnsureLoaded();

    private const int CenterX = 8;
    private const int CenterZ = 8;

    private const float Dt = 0.1f;

    // Two floors (bands -12..-6 and -6..-1) with no piles or doors of their own: the altar stands at (8, 0, 8), the
    // test world's stone floor, so the king rises with its feet on the floor at y = 1
    private static TowerRecord Record() => TowerRecord.Build((5, -3), CenterX, CenterZ, 12,
    [
        (-12, -6, System.Array.Empty<(int, int, int)>(), System.Array.Empty<(int, int, int)>()),
        (-6, -1, System.Array.Empty<(int, int, int)>(), System.Array.Empty<(int, int, int)>()),
    ], hasSite: false);

    private static TowerManager Authoritative() => Authoritative(out _);

    private static TowerManager Authoritative(out ChunkManager chunks)
    {
        chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var writer = new TowerBlockWriter(chunks, new FluidManager(), new LeafDecayManager(), new FallingBlockManager(),
            new LodManager(null!), (_, _, _) => { }, null);
        return new TowerManager(writer, (_, _) => null);
    }

    private static SkeletonKingManager Kings() => new(EntityRegistry.Require(SkeletonKing.EntityName));

    // Opens every door, begins the ritual and runs the altars until the king has risen
    private static SkeletonKing Raise(TowerManager towers, TowerRecord record, SkeletonKingManager kings)
    {
        OpenAll(record);
        Assert.Equal(AltarVerdict.Begin, Begin(towers, record));
        var steps = (int)(MobConfig.SkeletonKingRitualSeconds / Dt) + 2;
        for (var i = 0; i < steps && record.AltarState == TowerAltarState.Ritual; i++)
            towers.UpdateAltars(Dt, kings, null);
        Assert.Equal(TowerAltarState.Awake, record.AltarState);
        return Assert.Single(kings.Mobs);
    }

    private static void OpenAll(TowerRecord record)
    {
        foreach (var floor in record.Floors) floor.DoorOpen = true;
    }

    private static AltarVerdict Begin(TowerManager towers, TowerRecord record)
    {
        var (x, y, z) = record.AltarCell;
        return towers.BeginRitual(x, y, z);
    }

    [Fact]
    public void TheRitualIsRefusedWhileAnyFloorIsSealed()
    {
        var towers = Authoritative();
        var record = towers.Track(Record());
        record.Floors[0].DoorOpen = true;

        Assert.Equal(AltarVerdict.Sealed, Begin(towers, record));
        Assert.Equal(TowerAltarState.Dormant, record.AltarState);

        // Opening the last door lifts the seal
        record.Floors[1].DoorOpen = true;
        Assert.Equal(AltarVerdict.Begin, Begin(towers, record));
    }

    [Fact]
    public void TheRitualBeginsOnceAndThenStirs()
    {
        var towers = Authoritative();
        var record = towers.Track(Record());
        OpenAll(record);

        Assert.Equal(AltarVerdict.Begin, Begin(towers, record));
        Assert.Equal(TowerAltarState.Ritual, record.AltarState);
        Assert.True(record.RitualLeft > 0f);

        // A repeated click starts nothing again: the timer is left running
        record.RitualLeft = 0.5f;
        Assert.Equal(AltarVerdict.Stirring, Begin(towers, record));
        Assert.Equal(TowerAltarState.Ritual, record.AltarState);
        Assert.Equal(0.5f, record.RitualLeft);
    }

    [Fact]
    public void ADefeatedAltarNeverBeginsAgain()
    {
        var towers = Authoritative();
        var record = towers.Track(Record());
        OpenAll(record);
        towers.MarkDefeated(record);

        Assert.True(record.BossDefeated);
        Assert.Equal(AltarVerdict.Defeated, Begin(towers, record));
        Assert.Equal(TowerAltarState.Defeated, record.AltarState);
    }

    [Fact]
    public void AMirrorJudgesButNeverBegins()
    {
        var mirror = new TowerManager(null);
        var record = mirror.Track(Record());
        var (x, y, z) = record.AltarCell;

        Assert.Equal(AltarVerdict.Sealed, mirror.JudgeAltar(x, y, z));
        OpenAll(record);
        Assert.Equal(AltarVerdict.Begin, mirror.JudgeAltar(x, y, z));
        Assert.Equal(AltarVerdict.Begin, mirror.BeginRitual(x, y, z));
        Assert.Equal(TowerAltarState.Dormant, record.AltarState);
    }

    [Fact]
    public void OnlyTheAltarCellCounts()
    {
        var towers = Authoritative();
        var record = towers.Track(Record());
        OpenAll(record);
        var (x, y, z) = record.AltarCell;

        Assert.Equal(AltarVerdict.NoTower, towers.BeginRitual(x, y - 1, z));
        Assert.Equal(AltarVerdict.NoTower, towers.BeginRitual(x + 1, y, z));
        Assert.Equal(AltarVerdict.NoTower, towers.BeginRitual(x + 500, y, z));
        Assert.Equal(TowerAltarState.Dormant, record.AltarState);
    }

    [Fact]
    public void TheRecordsAltarCellIsTheSites()
    {
        foreach (var site in TowerSiteFixture.SyntheticSites())
            Assert.Equal(site.AltarCell, TowerRecord.FromSite(site).AltarCell);
    }

    [Fact]
    public void ADefeatIsAnnouncedOnce()
    {
        var towers = Authoritative();
        var record = towers.Track(Record());
        var announced = 0;
        towers.BossDefeated += r =>
        {
            Assert.Same(record, r);
            announced++;
        };

        towers.MarkDefeated(record);
        towers.MarkDefeated(record);
        Assert.Equal(1, announced);
    }

    [Fact]
    public void TheKingRisesOnceAfterTheRitual()
    {
        var towers = Authoritative(out var chunks);
        var record = towers.Track(Record());
        var kings = Kings();
        OpenAll(record);
        Begin(towers, record);

        // Nothing rises while the ritual still runs
        towers.UpdateAltars(MobConfig.SkeletonKingRitualSeconds * 0.5f, kings, null);
        Assert.Equal(TowerAltarState.Ritual, record.AltarState);
        Assert.Empty(kings.Mobs);

        towers.UpdateAltars(MobConfig.SkeletonKingRitualSeconds, kings, null);
        Assert.Equal(TowerAltarState.Awake, record.AltarState);
        var king = Assert.Single(kings.Mobs);
        Assert.Equal(record.Key, king.Home.Tower);
        // It stands over the altar, its feet on top of the altar block
        var (x, y, z) = record.AltarCell;
        Assert.Equal(x + 0.5f, king.Position.X, 3);
        Assert.Equal(z + 0.5f, king.Position.Z, 3);
        Assert.Equal(y + 1f, king.Position.Y - king.BodyHalfHeight, 3);

        // The fight goes on: later frames never raise a second king
        var drops = new DropManager();
        IReadOnlyList<IMobTarget> targets = [new StubTarget(StubTarget.EyeAt(new Vector3(x + 6.5f, 1f, z + 0.5f)))];
        for (var i = 0; i < 60; i++)
        {
            kings.Update(1f / 60f, chunks, drops, targets);
            towers.UpdateAltars(1f / 60f, kings, null);
        }
        Assert.Single(kings.Mobs);
        Assert.Equal(TowerAltarState.Awake, record.AltarState);
        Assert.Equal(AltarVerdict.Awake, Begin(towers, record));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AKingThatLeavesUnkilledFreesTheRitual(bool nobodyInPlay)
    {
        var towers = Authoritative(out var chunks);
        var record = towers.Track(Record());
        var kings = Kings();
        var king = Raise(towers, record, kings);
        var drops = new DropManager();
        IReadOnlyList<IMobTarget> targets = nobodyInPlay
            ? []
            : [new StubTarget(StubTarget.EyeAt(new Vector3(CenterX + 500f, 1f, CenterZ)))];

        kings.Update(1f / 60f, chunks, drops, targets);
        towers.UpdateAltars(1f / 60f, kings, null);

        Assert.True(king.IsDying);
        Assert.True(king.Health > 0);
        Assert.Equal(TowerAltarState.Dormant, record.AltarState);
        Assert.False(record.BossDefeated);
        Assert.Equal(AltarVerdict.Begin, Begin(towers, record));
    }

    [Fact]
    public void APendingRestoreKeepsTheTowerAwake()
    {
        var towers = Authoritative();
        var record = towers.Track(Record());
        var kings = Kings();
        OpenAll(record);
        record.AltarState = TowerAltarState.Awake;

        // The saved king still waits for its chunk: the tower stays awake with no king standing
        for (var i = 0; i < 10; i++) towers.UpdateAltars(Dt, kings, key => key == record.Key);
        Assert.Equal(TowerAltarState.Awake, record.AltarState);
        Assert.Empty(kings.Mobs);

        // A pending king of another tower does not hold this one
        towers.UpdateAltars(Dt, kings, key => key != record.Key);
        Assert.Equal(TowerAltarState.Dormant, record.AltarState);
    }

    [Fact]
    public void AKillDefeatsTheTowerTheSameFrame()
    {
        var towers = Authoritative(out var chunks);
        var record = towers.Track(Record());
        var kings = Kings();
        var announced = 0;
        towers.BossDefeated += _ => announced++;
        var king = Raise(towers, record, kings);

        // Killed with no king update in between: the death has not even started
        king.TakeDamage(king.Health);
        Assert.False(king.IsDying);
        towers.SettleAltars(kings, null);
        Assert.Equal(TowerAltarState.Defeated, record.AltarState);
        Assert.Equal(1, announced);

        // Its death plays out and it is removed: the tower stays defeated and is announced once
        var drops = new DropManager();
        for (var i = 0; i < 600 && kings.Mobs.Count > 0; i++)
        {
            kings.Update(1f / 60f, chunks, drops, []);
            towers.UpdateAltars(1f / 60f, kings, null);
            Assert.Equal(TowerAltarState.Defeated, record.AltarState);
        }
        Assert.Empty(kings.Mobs);
        Assert.Equal(1, announced);
        Assert.Equal(AltarVerdict.Defeated, Begin(towers, record));
        towers.UpdateAltars(MobConfig.SkeletonKingRitualSeconds * 2f, kings, null);
        Assert.Empty(kings.Mobs);
    }

    [Fact]
    public void AMirrorNeverRaisesTheKing()
    {
        var mirror = new TowerManager(null);
        var record = mirror.Track(Record());
        var kings = Kings();
        OpenAll(record);
        record.AltarState = TowerAltarState.Ritual;
        record.RitualLeft = 0f;

        for (var i = 0; i < 10; i++) mirror.UpdateAltars(Dt, kings, null);
        mirror.SettleAltars(kings, null);

        Assert.Empty(kings.Mobs);
        Assert.Equal(TowerAltarState.Ritual, record.AltarState);
    }

    [Fact]
    public void ADefeatedTowerStartsCollapsingAndAnUndefeatedOneNever()
    {
        var skeletons = new SkeletonManager(EntityRegistry.Require(Skeleton.EntityName));
        foreach (var defeat in new[] { false, true })
        {
            var towers = Authoritative(out var chunks);
            // The fixture's tower stands far from the loaded test chunks, so its cells wait unloaded
            var record = towers.Track(TowerRecord.FromSite(TowerSiteFixture.SiteOf(WorldGenerationConfig.TowerMinRadius, 3, 0)));
            var siteless = towers.Track(Record());
            Assert.NotEqual(record.Key, siteless.Key);
            if (defeat) towers.MarkDefeated(record);
            towers.MarkDefeated(siteless);

            for (var i = 0; i < 3; i++) towers.Update(Dt, chunks, skeletons, [], null, null);

            Assert.Equal(defeat, record.Collapse != null);
            Assert.False(record.Collapse?.Done ?? false);
            // A record with no world site has nothing to collapse
            Assert.Null(siteless.Collapse);
        }
    }
}
