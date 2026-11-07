using System;
using VoxelEngine.WorldGen;
using System.Collections.Generic;
using System.Linq;

namespace VoxelEngine.Content.Structures;

/// <summary>Every structure template by kind, published once by <c>ContentRegistry.Load</c>, then read-only.</summary>
public static class StructureRegistry
{
    /// <summary>The largest footprint edge a template may have: the placements' chunk reach assumes it.</summary>
    public const int MaxSize = 17;

    /// <summary>The tallest a template may be.</summary>
    public const int MaxHeight = 24;

    private static StructureTemplate[] _all = [];
    private static StructureTemplate[][] _byKind = [[], [], [], []];

    /// <summary>Every template, in file-name order.</summary>
    public static IReadOnlyList<StructureTemplate> All => _all;

    /// <summary>The templates of one kind, in file-name order.</summary>
    public static IReadOnlyList<StructureTemplate> Of(StructureKind kind) => _byKind[(int)kind];

    /// <summary>Picks a template of the kind (and, for surface, the biome) from a unit roll by weight; false when none qualifies.</summary>
    public static bool TryPick(StructureKind kind, Biome? biome, float roll, out StructureTemplate template)
    {
        template = null!;
        var total = 0f;
        foreach (var t in _byKind[(int)kind])
            if (Allows(t, biome)) total += t.Weight;
        if (total <= 0f) return false;

        var target = roll * total;
        foreach (var t in _byKind[(int)kind])
        {
            if (!Allows(t, biome)) continue;
            template = t;
            target -= t.Weight;
            if (target < 0f) return true;
        }
        return true;
    }

    private static bool Allows(StructureTemplate t, Biome? biome) =>
        t.Biomes == null || biome == null || Array.IndexOf(t.Biomes, biome.Value) >= 0;

    internal static void Publish(IEnumerable<StructureTemplate> templates)
    {
        _all = templates.ToArray();
        _byKind = Enum.GetValues<StructureKind>().Select(k => _all.Where(t => t.Kind == k).ToArray()).ToArray();
    }
}
