using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Simulation.Chests;

/// <summary>
/// One slot of a chest: a stackable (a block or misc id and how many) or one tool or piece of gear
/// (<paramref name="Instance"/>, which keeps every component it carries, <paramref name="Count"/> 1). Empty is id 0.
/// </summary>
public readonly record struct ChestSlot(ushort Id, int Count, ItemStack? Instance)
{
    /// <summary>The empty slot.</summary>
    public static readonly ChestSlot Empty = default;

    /// <summary>Whether nothing is in the slot.</summary>
    public bool IsEmpty => Id == 0 || Count <= 0;
}
