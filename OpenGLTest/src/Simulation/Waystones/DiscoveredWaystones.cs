using System.Collections.Generic;
using System.Linq;

namespace VoxelEngine.Simulation.Waystones;

/// <summary>
/// The waystones one player has discovered (<see cref="UI.InventoryPanel.Inventory.Waystones"/>), GL-free: a set of
/// cells, each with the name the waystone had when this player found it, so a client without the world's registry row
/// still has something to show. The registry (<see cref="WaystoneManager"/>) stays the source of the current name and
/// the lost flag. Saved by cell in <c>player.json</c> and carried in the inventory snapshot.
/// </summary>
public sealed class DiscoveredWaystones
{
    private readonly Dictionary<(int X, int Y, int Z), string> _entries = new();

    /// <summary>One discovered waystone: its cell and the name it was given here.</summary>
    public readonly record struct Entry(int X, int Y, int Z, string Name);

    /// <summary>Moves whenever the set or a name really changes (never for an equal import), so a list over it
    /// rebuilds only then.</summary>
    public int Version { get; private set; }

    /// <summary>How many waystones this player has discovered.</summary>
    public int Count => _entries.Count;

    /// <summary>Whether this player has discovered the waystone at the cell.</summary>
    public bool Contains(int x, int y, int z) => _entries.ContainsKey((x, y, z));

    /// <summary>The name this player knows the waystone at the cell by, when discovered.</summary>
    public bool TryGetName(int x, int y, int z, out string name) => _entries.TryGetValue((x, y, z), out name!);

    /// <summary>Every entry, ordered by cell (x, then y, then z), so a save is the same for the same set.</summary>
    public IEnumerable<Entry> Entries =>
        _entries.OrderBy(e => e.Key.X).ThenBy(e => e.Key.Y).ThenBy(e => e.Key.Z)
            .Select(e => new Entry(e.Key.X, e.Key.Y, e.Key.Z, e.Value));

    /// <summary>Records the waystone at the cell as discovered under <paramref name="name"/>; returns whether it was
    /// new. A waystone discovered before keeps the name it had then.</summary>
    public bool Add(int x, int y, int z, string name)
    {
        if (!_entries.TryAdd((x, y, z), name)) return false;
        Version++;
        return true;
    }

    /// <summary>Updates the name of a discovered waystone (its first discoverer renamed it); false when this player
    /// never discovered it.</summary>
    public bool Rename(int x, int y, int z, string name)
    {
        if (!_entries.TryGetValue((x, y, z), out var old)) return false;
        if (old == name) return true;
        _entries[(x, y, z)] = name;
        Version++;
        return true;
    }

    /// <summary>Replaces the whole set (a save or a server snapshot); an equal set does not move
    /// <see cref="Version"/>, so a client importing every snapshot never rebuilds an open list.</summary>
    internal void Replace(IEnumerable<Entry> entries)
    {
        var next = new Dictionary<(int X, int Y, int Z), string>();
        foreach (var entry in entries)
            next.TryAdd((entry.X, entry.Y, entry.Z), entry.Name);
        if (next.Count == _entries.Count && next.All(e => _entries.TryGetValue(e.Key, out var name) && name == e.Value))
            return;
        _entries.Clear();
        foreach (var (key, name) in next) _entries[key] = name;
        Version++;
    }
}
