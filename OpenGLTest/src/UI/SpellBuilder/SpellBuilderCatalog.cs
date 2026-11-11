using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Spells;
using VoxelEngine.Magic;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.UI.SpellBuilder;

/// <summary>
/// Which spell parts the builder offers: the registry's parts of each kind that the player knows
/// (<see cref="KnownSpellParts.Knows"/>, the one known-part rule), in key order. GL-free.
/// </summary>
public static class SpellBuilderCatalog
{
    /// <summary>The builder's four columns, left to right.</summary>
    public static readonly SpellPartKind[] Columns =
    [
        SpellPartKind.Element, SpellPartKind.Shape, SpellPartKind.Modifier, SpellPartKind.Trigger,
    ];

    /// <summary>The known parts of <paramref name="kind"/>, ordered by key (ordinal). Allocates: call it on a rebuild only.</summary>
    public static IReadOnlyList<SpellPart> Known(SpellPartKind kind, Inventory inventory, bool creative) =>
        SpellPartRegistry.All
            .Where(p => p.Kind == kind && KnownSpellParts.Knows(inventory, p, creative))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToArray();
}
