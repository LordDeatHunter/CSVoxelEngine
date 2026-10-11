using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// Static registry of every <see cref="TreeSpecies"/>, published once by
/// <see cref="ContentRegistry.Load"/> before the <see cref="World"/> (and any worker thread)
/// exists, then read-only. A species is keyed by its file name under <c>assets/data/trees</c>.
/// </summary>
public static class TreeSpeciesRegistry
{
    private static Dictionary<string, TreeSpecies> _byName = new(StringComparer.Ordinal);

    /// <summary>Every registered species by file name.</summary>
    public static IReadOnlyDictionary<string, TreeSpecies> ByName => _byName;

    /// <summary>Every registered species.</summary>
    public static IEnumerable<TreeSpecies> All => _byName.Values;

    /// <summary>The species registered under that file name, or null.</summary>
    public static TreeSpecies? TryGet(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    /// The species whose wood or leaves <paramref name="blockId"/> is, or null for any other block.
    /// Two species sharing one wood (allowed when they agree on the rim) resolve to the first in
    /// file order, which is all a use that grows the column's tree needs.
    /// </summary>
    public static TreeSpecies? ForBlock(byte blockId)
    {
        foreach (var species in _byName.Values)
        {
            if (species.WoodId == blockId || (species.LeavesId != 0 && species.LeavesId == blockId))
                return species;
        }
        return null;
    }

    /// <summary>The species registered under that file name; throws naming the missing content.</summary>
    public static TreeSpecies Require(string name)
    {
        if (_byName.TryGetValue(name, out var species)) return species;
        throw new InvalidOperationException($"No tree species named '{name}' is registered.");
    }

    internal static void Publish(Dictionary<string, TreeSpecies> species) => _byName = species;

    internal static void Reset() => _byName = new Dictionary<string, TreeSpecies>(StringComparer.Ordinal);
}
