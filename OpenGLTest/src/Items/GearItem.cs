using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>
/// A piece of wearable gear: armor or an accessory. It stores in the Gear pool one instance at a time, so a
/// piece's rolled modifiers (a component on its <see cref="ItemStack"/>) stay its own. What it fits and what it
/// changes is <see cref="Info"/>, compiled from the item JSON's <c>equip</c> object.
/// </summary>
public sealed class GearItem : Item
{
    internal GearItem(ItemFactory.ItemSpec spec) : base(spec)
    {
        Info = spec.Equip ?? throw new System.InvalidOperationException("A gear item needs an equip object.");
    }

    /// <summary>What the piece fits and changes.</summary>
    public GearInfo Info { get; }

    /// <summary>Gear has its own pool, apart from the tools.</summary>
    public override InventoryCategory StorageCategory => InventoryCategory.Gear;
}
