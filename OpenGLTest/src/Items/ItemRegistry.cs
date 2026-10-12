using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content;
using VoxelEngine.Content.Blocks;

namespace VoxelEngine.Items;

/// <summary>
/// Static singletons for every item: pure items from <c>assets/data/items/</c> plus one
/// <see cref="BlockItem"/> per placeable block, all keyed by the shared byte id.
/// Published once by <see cref="ContentRegistry.Load"/> before the <see cref="World"/> (and any
/// worker thread) exists, then read-only. Gameplay resolves items here instead of naming ids.
/// </summary>
public static class ItemRegistry
{
    private static Dictionary<byte, Item> _byId = new();
    private static Dictionary<string, byte> _names = new(System.StringComparer.Ordinal);

    /// <summary>Every registered item by id, pure items and block items alike.</summary>
    public static IReadOnlyDictionary<byte, Item> ById => _byId;

    /// <summary>Every registered item; prefer <see cref="TryGet"/> by id on hot paths.</summary>
    public static IEnumerable<Item> All => _byId.Values;

    /// <summary>Whether an id is a pure item rather than a block (or air/unknown). Blocks wrapped
    /// as <see cref="BlockItem"/> are placeable and deliberately excluded.</summary>
    public static bool IsItem(byte id) => _byId.TryGetValue(id, out var item) && item is not BlockItem;

    /// <summary>Item singleton for an id, or null for air/unknown ids.</summary>
    public static Item? TryGet(byte id) => _byId.GetValueOrDefault(id);

    /// <summary>The pure item registered under that JSON file name; throws naming the missing
    /// content at startup instead of silently mis-indexing.</summary>
    public static Item RequireItem(string name)
    {
        if (_names.TryGetValue(name, out var id) && _byId.TryGetValue(id, out var item))
            return item;
        throw new System.InvalidOperationException($"No item named '{name}' is registered.");
    }

    /// <summary>First pure item declaring this use action, or null when none does. Used for spawn
    /// stock (the first pickup_fluid item is the starting bucket).</summary>
    public static Item? FindByAction(ItemUseAction action) =>
        _byId.Values.FirstOrDefault(i => i is not BlockItem && i.UseAction == action);

    /// <summary>Pure item with this display name, or null. Used for spawn stock (compass).</summary>
    public static Item? FindByName(string name) =>
        _byId.Values.FirstOrDefault(i => i is not BlockItem
            && i.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));

    internal static void Publish(Dictionary<byte, Item> items, Dictionary<string, byte> names)
    {
        _byId = items;
        _names = names;
    }

    internal static void PublishBlockItems(IEnumerable<BlockItem> blocks)
    {
        foreach (var block in blocks)
        {
            if (_byId.ContainsKey(block.Id))
                throw new System.InvalidOperationException(
                    $"Block item '{block.Name}' collides with a registered item id {block.Id}.");
            _byId[block.Id] = block;
        }
    }

    internal static void Reset()
    {
        _byId = new Dictionary<byte, Item>();
        _names = new Dictionary<string, byte>(System.StringComparer.Ordinal);
    }
}
