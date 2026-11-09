using System.Linq;
using System.Numerics;
using VoxelEngine.Simulation.Tower;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Tower;

/// <summary>
/// The tower lock rules (<see cref="TowerRules"/>): a pile wakes only for a target in range on its own floor, a door
/// opens only once its floor is awake and cleared, spawn points skip walled columns and a bound skeleton strays only
/// past its leash.
/// </summary>
public sealed class TowerRulesTests
{
    private const float Radius = 8f;

    [Fact]
    public void APileWakesOnlyForATargetInRangeOnItsOwnFloor()
    {
        var pile = (X: 10, Y: 1, Z: 10);
        const int bottom = 0, top = 6;
        var center = new Vector3(10.5f, 3f, 10.5f);

        Assert.True(TowerRules.PileTriggers(pile, bottom, top, center, Radius));
        Assert.True(TowerRules.PileTriggers(pile, bottom, top, center + new Vector3(Radius * 0.99f, 0f, 0f), Radius));
        Assert.False(TowerRules.PileTriggers(pile, bottom, top, center + new Vector3(Radius * 1.01f, 0f, 0f), Radius));
        Assert.False(TowerRules.PileTriggers(pile, bottom, top,
            center + new Vector3(Radius * 0.75f, 0f, Radius * 0.75f), Radius));

        // The band's top belongs to the floor, its slab to the floor below
        Assert.True(TowerRules.PileTriggers(pile, bottom, top, center with { Y = top }, Radius));
        Assert.False(TowerRules.PileTriggers(pile, bottom, top, center with { Y = bottom }, Radius));
        Assert.False(TowerRules.PileTriggers(pile, bottom, top, center with { Y = top + 0.5f }, Radius));
    }

    [Fact]
    public void ADoorOpensOnlyOnceItsFloorIsClearedAndThePreviousDoorIsOpen()
    {
        Assert.True(TowerRules.FloorAwake(0, previousDoorOpen: false));
        Assert.False(TowerRules.FloorAwake(1, previousDoorOpen: false));
        Assert.True(TowerRules.FloorAwake(1, previousDoorOpen: true));

        Assert.True(TowerRules.DoorMayOpen(0, false, pilesLeft: 0, alive: 0));
        Assert.True(TowerRules.DoorMayOpen(2, true, pilesLeft: 0, alive: 0));
        Assert.False(TowerRules.DoorMayOpen(2, false, pilesLeft: 0, alive: 0));
        Assert.False(TowerRules.DoorMayOpen(0, false, pilesLeft: 1, alive: 0));
        Assert.False(TowerRules.DoorMayOpen(0, false, pilesLeft: 0, alive: 1));
    }

    [Fact]
    public void SpawnPointsSkipSolidColumns()
    {
        const int x = 5, y = 1, z = 5;
        // The pile's own column and the +X column are walled (one at the feet, one at the head)
        bool IsOpen(int cx, int cy, int cz) => !(cx == x && cz == z && cy == y) && !(cx == x + 1 && cz == z && cy == y + 1);

        var points = TowerRules.SpawnPoints(x, y, z, 9, IsOpen);
        Assert.Equal(9 - 2, points.Count);
        Assert.DoesNotContain(points, p => (int)p.X == x && (int)p.Z == z);
        Assert.DoesNotContain(points, p => (int)p.X == x + 1 && (int)p.Z == z);
        Assert.Equal(points.Count, points.Distinct().Count());
        Assert.All(points, p => Assert.True(p.Y > y && p.Y < y + 2));

        var few = TowerRules.SpawnPoints(x, y, z, 2, IsOpen);
        Assert.Equal(2, few.Count);
        Assert.Equal(points.Take(2), few);

        Assert.Empty(TowerRules.SpawnPoints(x, y, z, 4, (_, _, _) => false));
    }

    [Fact]
    public void ASkeletonStraysOnlyPastTheLeash()
    {
        const int cx = 100, cz = -40;
        const float leash = 20f;
        var center = new Vector3(cx + 0.5f, 7f, cz + 0.5f);

        Assert.False(TowerRules.Strayed(center, cx, cz, leash));
        Assert.False(TowerRules.Strayed(center + new Vector3(0f, 500f, leash * 0.99f), cx, cz, leash));
        Assert.True(TowerRules.Strayed(center + new Vector3(leash * 1.01f, 0f, 0f), cx, cz, leash));
    }

    [Fact]
    public void ACollapseIsDueOnlyForADefeatedTowerThatHasNotFinished()
    {
        foreach (var altar in System.Enum.GetValues<TowerAltarState>())
        foreach (var done in new[] { false, true })
            Assert.Equal(altar == TowerAltarState.Defeated && !done, TowerRules.CollapseDue(altar, done));
    }
}
