using VoxelEngine.Simulation.Waystones;

namespace VoxelEngine.UI.Waystone;

/// <summary>
/// One row of the teleport list (<see cref="WaystoneRows.Build"/>): a discovered waystone's cell, the name it goes by,
/// its realm, its distance in whole blocks from the waystone the player stands at, whether its block was broken
/// (<see cref="Lost"/>) and whether it is the one the player stands at (<see cref="Current"/>).
/// </summary>
public readonly record struct WaystoneRow(int X, int Y, int Z, string Name, WaystoneRealm Realm, int Distance, bool Lost,
    bool Current)
{
    /// <summary>Whether a click on the row teleports: neither lost nor the current waystone.</summary>
    public bool Selectable => !Lost && !Current;
}
