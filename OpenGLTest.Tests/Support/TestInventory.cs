using VoxelEngine.Content.Blocks;
using VoxelEngine.Items;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// A pre-stocked <see cref="Inventory"/> for tests that need blocks and tools to hand. A real
/// player starts from <see cref="Inventory.Empty"/>; this stock exists only in the test project.
/// </summary>
internal static class TestInventory
{
    /// <summary>How many of every placeable block <see cref="Stocked"/> holds.</summary>
    internal const int StartingStock = 20;

    private const int MaterialStock = 50;

    internal static Inventory Stocked()
    {
        var inventory = Inventory.Empty();
        foreach (var type in BlockType.Types.Values)
        {
            if (!type.HasItem || type.Fluid) continue;
            inventory.Add(type.Id, type.Lantern ? 1 : StartingStock, autoEquip: false);
        }
        if (ItemRegistry.FindByAction(ItemUseAction.PickupFluid) is { } bucket)
            inventory.Add(bucket.Id, autoEquip: false);
        foreach (var name in new[] { "Copper Pickaxe", "Copper Axe", "Compass", "Obsidian Dagger" })
            if (ItemRegistry.FindByName(name) is { } item)
                inventory.Add(item.Id, autoEquip: false);
        foreach (var name in new[] { "Mud Brick", "Coal", "Crushed Copper" })
            if (ItemRegistry.FindByName(name) is { } item)
                inventory.Add(item.Id, MaterialStock, autoEquip: false);
        return inventory;
    }
}
