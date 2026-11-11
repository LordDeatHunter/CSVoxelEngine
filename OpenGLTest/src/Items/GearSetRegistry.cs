using System;
using System.Collections.Generic;

namespace VoxelEngine.Items;

/// <summary>Every gear set by file name, published once by <c>ContentRegistry.Load</c> before any world exists, then read-only.</summary>
public static class GearSetRegistry
{
    private static Dictionary<string, GearSet> _byName = new(StringComparer.Ordinal);

    /// <summary>Every set.</summary>
    public static IReadOnlyCollection<GearSet> All => _byName.Values;

    /// <summary>The set with that file name, if any.</summary>
    public static bool TryGet(string name, out GearSet set) => _byName.TryGetValue(name, out set!);

    internal static void Publish(IEnumerable<GearSet> sets)
    {
        var map = new Dictionary<string, GearSet>(StringComparer.Ordinal);
        foreach (var set in sets) map[set.Name] = set;
        _byName = map;
    }
}
