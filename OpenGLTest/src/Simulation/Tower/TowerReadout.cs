namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// The look-at status lines of a battle tower's skeleton piles and doors, built from a <see cref="TowerFloorState"/>
/// (live on the authoritative side, read from the wire on a client's mirror). Floors are numbered from 1 for the
/// player: floor index 0 reads "Floor 1".
/// </summary>
public static class TowerReadout
{
    /// <summary>A pile's line: how many of its floor's piles still raise skeletons and how many skeletons are left.</summary>
    public static string Pile(TowerFloorState floor) =>
        $"Floor {floor.Floor + 1}: {Count(floor.PilesLeft, "pile")}, {Count(floor.Alive, "skeleton")}";

    /// <summary>
    /// A closed door's line: the floor it seals and what still holds it shut, the floor below first
    /// (<paramref name="previousOpen"/> false), then the floor's piles, then its skeletons.
    /// </summary>
    public static string Door(TowerFloorState floor, bool previousOpen)
    {
        var name = floor.Floor + 1;
        if (!previousOpen) return $"Floor {name} sealed: clear floor {floor.Floor} first";
        if (floor.PilesLeft > 0) return $"Floor {name} sealed: {Count(floor.PilesLeft, "pile")} left";
        return $"Floor {name} sealed: {Count(floor.Alive, "skeleton")} left";
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
