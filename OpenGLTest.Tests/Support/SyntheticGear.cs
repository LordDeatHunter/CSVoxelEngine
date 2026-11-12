using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Items;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// Gear items with abilities and modifiers no content file declares, for tests of what worn gear does. A synthetic
/// piece borrows a real piece's id of the same slot, so the inventory files it in the Gear pool, while every stack
/// of it keeps the synthetic item.
/// </summary>
internal static class SyntheticGear
{
    /// <summary>A gear item for <paramref name="slot"/> granting <paramref name="abilities"/> and the stat
    /// <paramref name="modifiers"/> (keyed by their JSON names, e.g. <c>undeadDamage</c>).</summary>
    public static GearItem Create(GearSlot slot, string[] abilities, Dictionary<string, float>? modifiers = null)
    {
        var info = ItemLoader.ParseEquip(new ItemDefinition
        {
            Equip = new()
            {
                Slot = slot.ToString().ToLowerInvariant(),
                Abilities = abilities,
                Modifiers = modifiers,
            },
        }, false, "items/synthetic.json")!;
        var borrowed = ItemRegistry.All.OfType<GearItem>().First(g => g.Info.Slot == slot).Id;
        var spec = new ItemFactory.ItemSpec(borrowed, "Synthetic", "t", ItemUseAction.None,
            0, 0, 0, 1f, 0, 0f, 1, 1, 1, ItemAnimationMode.None, 8f,
            Vector3.Zero, Vector3.Zero, 1f, false, 0.5f, 300f, AttackPattern.Punch, 0.5f, 1f, null, "synthetic",
            Equip: info);
        return Assert.IsType<GearItem>(ItemFactory.Create("synthetic", spec));
    }

    /// <summary>Adds one instance of <paramref name="gear"/> to <paramref name="inventory"/> and returns its
    /// instance id.</summary>
    public static int Own(Inventory inventory, GearItem gear)
    {
        Assert.True(inventory.AddInstance(new ItemStack(1, gear)));
        return inventory.GearEntries().Last().InstanceId;
    }
}
