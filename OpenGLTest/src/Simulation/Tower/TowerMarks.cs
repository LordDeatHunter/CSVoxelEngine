using System.Collections.Generic;
using System.Linq;

namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// The battle towers one player has marked on their map with a tower map (<see cref="UI.InventoryPanel.Inventory.TowerMarks"/>),
/// GL-free: each tower's center column, in the order they were marked. Saved in <c>player.json</c> and carried in the
/// inventory snapshot, so a client draws its server player's marks. A tower whose king falls is forgotten
/// (<see cref="Remove"/>, called for every player when <see cref="TowerManager.BossDefeated"/> fires).
/// </summary>
public sealed class TowerMarks
{
    private readonly List<(int X, int Z)> _towers = [];

    /// <summary>Moves whenever the list really changes (never for an equal import), so the map's pins rebuild only then.</summary>
    public int Version { get; private set; }

    public int Count => _towers.Count;

    /// <summary>The marked towers' center columns, oldest first.</summary>
    public IReadOnlyList<(int X, int Z)> Towers => _towers;

    public bool Contains(int x, int z) => _towers.Contains((x, z));

    /// <summary>Marks the tower centered on (x, z); false when it was marked already.</summary>
    public bool Add(int x, int z)
    {
        if (Contains(x, z)) return false;
        _towers.Add((x, z));
        Version++;
        return true;
    }

    /// <summary>Forgets the tower centered on (x, z); false when it was not marked.</summary>
    public bool Remove(int x, int z)
    {
        if (!_towers.Remove((x, z))) return false;
        Version++;
        return true;
    }

    /// <summary>Replaces the whole list (a save or a server snapshot); an equal list does not move <see cref="Version"/>.</summary>
    internal void Replace(IEnumerable<(int X, int Z)> towers)
    {
        var next = towers.Distinct().ToList();
        if (next.SequenceEqual(_towers)) return;
        _towers.Clear();
        _towers.AddRange(next);
        Version++;
    }
}
