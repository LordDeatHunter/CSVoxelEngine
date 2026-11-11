using System;
using System.Collections.Generic;
using VoxelEngine.Simulation.Waystones;

namespace VoxelEngine.UI.Waystone;

/// <summary>
/// The teleport list's rows, GL-free: every waystone the player discovered whose name contains the search query
/// (case-insensitive), named and flagged lost by the world's registry when it holds a row (else by the name the player
/// found it under), the current waystone first, then the standing ones nearest first, then the lost ones.
/// </summary>
public static class WaystoneRows
{
    /// <summary>
    /// Fills <paramref name="rows"/> (cleared first). <paramref name="record"/> is the registry row of a cell or null,
    /// <paramref name="realm"/> the realm of a cell, <paramref name="current"/> the waystone the player stands at.
    /// </summary>
    public static void Build(List<WaystoneRow> rows, IEnumerable<DiscoveredWaystones.Entry> entries,
        Func<int, int, int, WaystoneRecord?> record, Func<int, int, int, WaystoneRealm> realm,
        (int X, int Y, int Z) current, string query)
    {
        rows.Clear();
        query = query.Trim();
        foreach (var entry in entries)
        {
            var found = record(entry.X, entry.Y, entry.Z);
            var name = found?.Name ?? entry.Name;
            if (query.Length > 0 && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var isCurrent = (entry.X, entry.Y, entry.Z) == current;
            rows.Add(new WaystoneRow(entry.X, entry.Y, entry.Z, name, realm(entry.X, entry.Y, entry.Z),
                Distance(current, (entry.X, entry.Y, entry.Z)), found?.Lost ?? false, isCurrent));
        }
        rows.Sort(Compare);
    }

    /// <summary>The distance in whole blocks (rounded) between two cells.</summary>
    public static int Distance((int X, int Y, int Z) a, (int X, int Y, int Z) b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return (int)Math.Round(Math.Sqrt(dx * dx + dy * dy + dz * dz));
    }

    private static int Compare(WaystoneRow a, WaystoneRow b)
    {
        if (a.Current != b.Current) return a.Current ? -1 : 1;
        if (a.Lost != b.Lost) return a.Lost ? 1 : -1;
        var byDistance = a.Distance.CompareTo(b.Distance);
        return byDistance != 0 ? byDistance : string.CompareOrdinal(a.Name, b.Name);
    }
}
