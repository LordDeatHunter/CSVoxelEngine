using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Saves;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Tower;

/// <summary>
/// The battle tower locks persist in <c>stations.json</c> (<see cref="TowerManager.Capture"/>,
/// <see cref="TowerManager.Restore"/>): a tower's piles, owed skeletons and open doors come back as they were, an open
/// door is swept again, and an entry that no longer fits a tower is dropped and counted. Real default-seed sites, so the
/// class runs in the seed collection.
/// </summary>
[Collection("Worldgen seed")]
public sealed class TowerManagerSaveTests
{
    private const int Window = 4;

    public TowerManagerSaveTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    private static TowerSite Nearest()
    {
        Assert.True(TowerPlacement.TryFindNearest(0, 0, Window, out var site), "no tower within the window");
        return site;
    }

    private static List<StationSaveData.TowerData>? RoundTrip(List<StationSaveData.TowerData>? towers) =>
        StationSaveCodec.TryParse(StationSaveCodec.ToBytes(new StationSaveData { Towers = towers }))!.Towers;

    [Fact]
    public void ATowerComesBackFromStationsJsonAsItWas()
    {
        var site = Nearest();
        var saved = new TowerManager(null);
        var record = saved.Track(site);
        Assert.True(record.Floors.Length >= 3);
        Assert.All(record.Floors, f => Assert.NotEmpty(f.Piles));
        foreach (var pile in record.Floors[0].Piles) pile.WavesLeft = 0;
        record.Floors[0].DoorOpen = true;
        record.Floors[1].Piles[0].WavesLeft = MobConfig.SkeletonPileWaves - 1;
        record.Floors[2].Owed = 3;

        var restored = new TowerManager(null);
        Assert.Equal(0, restored.Restore(RoundTrip(saved.Capture())));

        var back = restored.RecordAt(site.CenterX, site.CenterZ);
        Assert.NotNull(back);
        Assert.Equal(record.Key, back!.Key);
        Assert.True(back.HasSite);
        Assert.Equal(record.Floors.Length, back.Floors.Length);
        for (var k = 0; k < record.Floors.Length; k++)
        {
            var was = record.Floors[k];
            var now = back.Floors[k];
            Assert.Equal(was.Piles.Select(p => p.WavesLeft), now.Piles.Select(p => p.WavesLeft));
            Assert.Equal(was.Piles.Select(p => p.Cell), now.Piles.Select(p => p.Cell));
            Assert.Equal(was.Owed, now.Owed);
            Assert.Equal(was.DoorOpen, now.DoorOpen);
            Assert.Equal(now.CountPilesLeft(), now.PilesLeft);
            // An open door is swept again, a closed one has nothing to sweep
            Assert.Equal(now.DoorOpen ? now.DoorCells.Count : 0, now.DoorRemaining.Count);
        }
    }

    [Theory]
    [InlineData(TowerAltarState.Ritual)]
    [InlineData(TowerAltarState.Awake)]
    [InlineData(TowerAltarState.Defeated)]
    public void TheAltarStateComesBackFromStationsJson(TowerAltarState state)
    {
        var site = Nearest();
        var saved = new TowerManager(null);
        saved.Track(site).AltarState = state;

        var restored = new TowerManager(null);
        Assert.Equal(0, restored.Restore(RoundTrip(saved.Capture())));
        var back = restored.RecordAt(site.CenterX, site.CenterZ)!;
        Assert.Equal(state, back.AltarState);
        // A ritual under way restarts its timer
        Assert.Equal(state == TowerAltarState.Ritual, back.RitualLeft > 0f);
    }

    [Fact]
    public void AnEntryWithoutAnAltarStateRestoresDormant()
    {
        var site = Nearest();
        var saved = new TowerManager(null);
        saved.Track(site).AltarState = TowerAltarState.Defeated;
        var entry = RoundTrip(saved.Capture())!.Single();
        entry.Altar = null;
        var unknown = RoundTrip(saved.Capture())!.Single();
        unknown.Altar = "NotAState";

        foreach (var data in new[] { entry, unknown })
        {
            var restored = new TowerManager(null);
            Assert.Equal(0, restored.Restore([data]));
            Assert.Equal(TowerAltarState.Dormant, restored.RecordAt(site.CenterX, site.CenterZ)!.AltarState);
        }

        // A dormant altar is not written at all
        var dormant = new TowerManager(null);
        dormant.Track(site);
        Assert.Null(dormant.Capture()!.Single().Altar);
    }

    [Fact]
    public void AnEntryWithNoTowerIsDroppedAndCounted()
    {
        var site = Nearest();
        var good = new TowerManager(null);
        good.Track(site);
        var valid = good.Capture()!.Single();

        var empty = (X: 0, Z: 0);
        var found = false;
        for (var cx = -Window; cx <= Window && !found; cx++)
        for (var cz = -Window; cz <= Window && !found; cz++)
        {
            if (TowerPlacement.TryGetSite(cx, cz, out _)) continue;
            empty = (cx, cz);
            found = true;
        }
        Assert.True(found, "every cell near the origin holds a tower");

        StationSaveData.TowerData Copy() => StationSaveCodec.TryParse(StationSaveCodec.ToBytes(
            new StationSaveData { Towers = [valid] }))!.Towers!.Single();

        var noTower = Copy();
        noTower.Cell = [empty.X, empty.Z];
        var badCell = Copy();
        badCell.Cell = [site.CellX];
        var extraFloor = Copy();
        extraFloor.Floors!.Add(new StationSaveData.TowerFloorData { Waves = [] });
        var badWave = Copy();
        badWave.Floors![0].Waves![0] = MobConfig.SkeletonPileWaves + 1;
        var badOwed = Copy();
        badOwed.Floors![0].Owed = -1;
        var badPiles = Copy();
        badPiles.Floors![0].Waves = [.. badPiles.Floors[0].Waves!, 0];

        var towers = new TowerManager(null);
        var bad = new List<StationSaveData.TowerData> { noTower, badCell, extraFloor, badWave, badOwed, badPiles };
        Assert.Equal(bad.Count, towers.Restore(RoundTrip(bad)));
        Assert.False(towers.HasAny);

        // The valid entry beside them still comes back
        Assert.Equal(bad.Count, towers.Restore(RoundTrip([.. bad, valid])));
        Assert.NotNull(towers.RecordAt(site.CenterX, site.CenterZ));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACollapseComesBackFromStationsJson(bool done)
    {
        var site = Nearest();
        var saved = new TowerManager(null);
        var record = saved.Track(site);
        record.AltarState = TowerAltarState.Defeated;
        var waiting = new[] { VoxelCoords.WorldToChunk(site.CenterX), VoxelCoords.WorldToChunk(site.GroundY),
            VoxelCoords.WorldToChunk(site.CenterZ) };
        record.Collapse = TowerCollapse.ForSite(site, 0f);
        record.Collapse.Apply(new StationSaveData.TowerCollapseData
        {
            NextY = site.GroundY + 3,
            Waiting = [waiting],
            Done = done,
        });

        var restored = new TowerManager(null);
        Assert.Equal(0, restored.Restore(RoundTrip(saved.Capture())));
        var back = restored.RecordAt(site.CenterX, site.CenterZ)!.Collapse;
        Assert.NotNull(back);
        Assert.Equal(record.Collapse.Done, back!.Done);
        Assert.Equal(record.Collapse.NextY, back.NextY);
        Assert.Equal(record.Collapse.Waiting.OrderBy(c => c), back.Waiting.OrderBy(c => c));
        Assert.Equal(done, back.Waiting.Count == 0);
        // A restored collapse starts at once
        Assert.Equal(0f, back.DelayLeft);
    }

    [Fact]
    public void ACollapseEntryOnATowerThatIsNotDefeatedIsIgnored()
    {
        var site = Nearest();
        var saved = new TowerManager(null);
        saved.Track(site);
        var entry = RoundTrip(saved.Capture())!.Single();
        entry.Collapse = new StationSaveData.TowerCollapseData { NextY = site.GroundY, Done = false };

        var restored = new TowerManager(null);
        Assert.Equal(0, restored.Restore([entry]));
        var back = restored.RecordAt(site.CenterX, site.CenterZ)!;
        Assert.Equal(TowerAltarState.Dormant, back.AltarState);
        Assert.Null(back.Collapse);
    }
}
