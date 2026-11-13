using System;
using System.Numerics;
using VoxelEngine.Items;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Tower;

/// <summary>
/// The tower map's rules (<see cref="TowerMarking"/>, <see cref="TowerMarks"/>): each use marks the next nearest tower
/// the player has neither marked nor conquered, a conquered tower is skipped and leaves the map of everyone who marked
/// it (the live <see cref="TowerManager.BossDefeated"/> hook and the join prune), and the marks survive the save.
/// </summary>
[Collection("Worldgen seed")]
public sealed class TowerMarkingTests : IDisposable
{
    private World? _world;

    public TowerMarkingTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    public void Dispose()
    {
        _world?.SaveAndDispose();
        WorldSeed.Apply(WorldSeed.Default);
    }

    // A defeated (or standing) record over the tower centered at (x, z), the way the tower manager keeps one
    private static TowerRecord RecordAt(TowerManager towers, StructureLocation tower, bool defeated)
    {
        var record = towers.Track(TowerRecord.Build((tower.X, tower.Z), tower.X, tower.Z, 9,
            [(tower.Y, tower.Y + 8, new[] { (tower.X, tower.Y, tower.Z) }, new[] { (tower.X, tower.Y + 2, tower.Z) })],
            hasSite: false));
        if (defeated) record.AltarState = TowerAltarState.Defeated;
        return record;
    }

    [Fact]
    public void EachUseMarksTheNextNearestTowerOut()
    {
        Assert.True(StructureLocator.TryFindTower(0, 0, WorldGenerationConfig.TowerLocateRings, out var nearest));
        var marks = new TowerMarks();

        var previous = -1.0;
        for (var i = 0; i < 3; i++)
        {
            Assert.True(TowerMarking.TryMarkNearest(marks, 0.5f, 0.5f, null, out var found));
            if (i == 0) Assert.Equal((nearest.X, nearest.Z), (found.X, found.Z));
            Assert.True(found.Distance >= previous, "a later map marked a nearer tower");
            previous = found.Distance;
        }

        Assert.Equal(3, marks.Count);
        Assert.Equal(3, new System.Collections.Generic.HashSet<(int, int)>(marks.Towers).Count);
    }

    [Fact]
    public void AConqueredTowerIsNeverMarked()
    {
        Assert.True(StructureLocator.TryFindTower(0, 0, WorldGenerationConfig.TowerLocateRings, out var nearest));
        var towers = new TowerManager(null, (_, _) => null);
        RecordAt(towers, nearest, defeated: true);
        var marks = new TowerMarks();

        Assert.True(TowerMarking.TryMarkNearest(marks, 0.5f, 0.5f, towers, out var found));

        Assert.NotEqual((nearest.X, nearest.Z), (found.X, found.Z));
        Assert.False(marks.Contains(nearest.X, nearest.Z));
    }

    [Fact]
    public void ATowerConqueredWhileAwayIsForgottenAndTheRestKept()
    {
        Assert.True(StructureLocator.TryFindTower(0, 0, WorldGenerationConfig.TowerLocateRings, out var nearest));
        var towers = new TowerManager(null, (_, _) => null);
        RecordAt(towers, nearest, defeated: true);
        var marks = new TowerMarks();
        marks.Add(nearest.X, nearest.Z);
        marks.Add(nearest.X + 5000, nearest.Z);

        Assert.True(TowerMarking.ForgetConquered(marks, towers));

        Assert.False(marks.Contains(nearest.X, nearest.Z));
        Assert.True(marks.Contains(nearest.X + 5000, nearest.Z));
        Assert.False(TowerMarking.ForgetConquered(marks, towers));
    }

    [Fact]
    public void UsingATowerMapInAWorldMarksTheNearestTowerAndItsFallTakesTheMarkAway()
    {
        Assert.True(StructureLocator.TryFindTower(0, 0, WorldGenerationConfig.TowerLocateRings, out var nearest));
        _world = new World(null!, null!, null!, null!, null!, null!, null, null);
        _world.Camera.TestTeleport(new Vector3(0.5f, 120f, 0.5f), yaw: -90f, pitch: 0f);
        var marks = _world.Inventory.TowerMarks;

        var result = ContentRegistry.RequireItem("tower_map").OnUse(new ItemUseContext(_world.ItemUse, 4f, 0), null);

        Assert.True(result.Applied);
        Assert.True(marks.Contains(nearest.X, nearest.Z));
        var record = RecordAt(_world.Towers, nearest, defeated: false);
        Assert.True(_world.Towers.DefeatAt(new Vector3(record.CenterX + 0.5f, nearest.Y, record.CenterZ + 0.5f)));
        Assert.False(marks.Contains(nearest.X, nearest.Z));
    }

    [Fact]
    public void TheMarksSurviveTheSaveInOrder()
    {
        var a = Inventory.Empty();
        a.TowerMarks.Add(300, -40);
        a.TowerMarks.Add(-2000, 77);
        var b = Inventory.Empty();

        b.Import(a.Export());

        Assert.Equal(a.TowerMarks.Towers, b.TowerMarks.Towers);
        // An equal import changes nothing, so the map's pins never rebuild for it
        var version = b.TowerMarks.Version;
        b.Import(a.Export());
        Assert.Equal(version, b.TowerMarks.Version);
    }
}
