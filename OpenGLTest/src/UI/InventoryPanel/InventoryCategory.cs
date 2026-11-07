namespace VoxelEngine.UI.InventoryPanel;

/// <summary>Which storage pool an id belongs to. Each pool has its own shared cap.</summary>
public enum InventoryCategory
{
    Blocks,
    Tools,
    Misc,
    Crafting,

    /// <summary>Wearable gear: instances like tools, but a pool and a cap of their own.</summary>
    Gear,
}
