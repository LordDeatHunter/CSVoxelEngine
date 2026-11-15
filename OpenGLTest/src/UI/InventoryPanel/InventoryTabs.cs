using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content;
using VoxelEngine.Items;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// Which panel tab lists an item and what a tab lists, apart from how the item is stored. A pool
/// (<see cref="Inventory.CategoryOf"/>) decides whether an id is a counted stack or an instance that carries its own
/// state (a lantern's fuel, a tool's wear); a tab (<see cref="TabOf"/>) is only where the panel shows it, so moving an
/// item between tabs changes <see cref="Item.TabCategory"/> and nothing in the storage, the saves or the network.
/// </summary>
public static class InventoryTabs
{
    /// <summary>One listed row: the id, its name, how many the player holds and, for an instance, which one (else 0).</summary>
    public readonly record struct TabRow(ushort Id, string Name, int Count, int InstanceId);

    /// <summary>The tab that lists an id, or null when it is not collectable (see <see cref="Inventory.CategoryOf"/>).</summary>
    public static InventoryCategory? TabOf(ushort id)
    {
        if (Inventory.CategoryOf(id) == null) return null;
        return BlockType.ById[id] != null ? InventoryCategory.Blocks : ItemRegistry.TryGet(id)?.TabCategory;
    }

    /// <summary>
    /// Everything the player holds that <paramref name="tab"/> lists, counted stacks and instances together, name-sorted
    /// (the fuller stack first, an older instance before a newer one). The Craft tab lists recipes, not items, so it is empty.
    /// </summary>
    public static IReadOnlyList<TabRow> Entries(Inventory inventory, InventoryCategory tab)
    {
        if (tab == InventoryCategory.Crafting) return [];
        var rows = new List<TabRow>();
        foreach (var pool in new[] { InventoryCategory.Blocks, InventoryCategory.Misc })
            foreach (var stack in inventory.StackEntries(pool))
                if (TabOf(stack.Id) == tab)
                    rows.Add(new TabRow(stack.Id, stack.Name, stack.Count, 0));
        foreach (var instance in inventory.ToolEntries().Concat(inventory.GearEntries()))
            if (TabOf(instance.ItemId) == tab)
                rows.Add(new TabRow(instance.ItemId, instance.Name, 1, instance.InstanceId));
        rows.Sort((a, b) =>
        {
            var name = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (name != 0) return name;
            var count = b.Count.CompareTo(a.Count);
            return count != 0 ? count : a.InstanceId.CompareTo(b.InstanceId);
        });
        return rows;
    }
}
