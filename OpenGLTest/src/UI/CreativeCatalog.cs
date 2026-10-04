using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// GL-free browsable catalog behind creative mode: every item that exists, grouped into the
/// same pools <see cref="Inventory"/> stores into, so the panel's Blocks/Tools/Misc tabs can
/// list the whole game instead of only what the player owns. Entries are not held by the
/// inventory; a click grants the id through <see cref="Inventory.Add"/>.
/// </summary>
public static class CreativeCatalog
{
    /// <summary>Amount the bulk "+" button grants when the quantity field is empty or invalid.</summary>
    public const int DefaultAmount = 64;

    private const int MaxAmount = Inventory.BlockCapacity;

    /// <summary>One catalog row: the id to grant, its display name and how many the player owns.</summary>
    public readonly record struct CatalogEntry(byte Id, string Name, int Count);

    /// <summary>
    /// Every item whose pool is <paramref name="category"/>, name-sorted, with the player's
    /// current count for each. Blocks come through their <see cref="BlockItem"/>, so a fluid or
    /// an itemless block is simply absent; a leafless block still appears under Misc if that is
    /// its pool.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> Entries(InventoryCategory category, Inventory inventory)
    {
        var entries = new List<CatalogEntry>();
        foreach (var item in ItemRegistry.All)
        {
            if (Inventory.CategoryOf(item.Id) != category) continue;
            entries.Add(new CatalogEntry(item.Id, item.Name, inventory.CountFor(item.Id)));
        }
        entries.Sort((a, b) =>
        {
            var name = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return name != 0 ? name : a.Id.CompareTo(b.Id);
        });
        return entries;
    }

    /// <summary>
    /// The bulk amount a quantity field holds: its positive integer, clamped to the block pool
    /// cap, or <see cref="DefaultAmount"/> for empty/garbage text (the field only accepts digits,
    /// so garbage is really only an empty edit).
    /// </summary>
    public static int ParseAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return DefaultAmount;
        if (!int.TryParse(text.Trim(), out var value) || value <= 0) return DefaultAmount;
        return Math.Min(value, MaxAmount);
    }
}
