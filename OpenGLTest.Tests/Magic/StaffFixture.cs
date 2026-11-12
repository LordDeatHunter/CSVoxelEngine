using System.Linq;
using VoxelEngine.Content.Spells;
using VoxelEngine.Items;
using VoxelEngine.Player;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Magic;

/// <summary>Builds a player holding a real staff, optionally with a valid recipe bound in one slot.</summary>
internal static class StaffFixture
{
    /// <summary>A shipped staff by its file name.</summary>
    public static StaffItem Staff(string name) => (StaffItem)ContentRegistry.RequireItem(name);

    /// <summary>The cheapest valid recipe the staff allows: one element and one shape from the registry.</summary>
    public static SpellPart[] ValidRecipe(StaffItem staff) =>
    [
        SpellPartRegistry.All.First(p => p.Kind == SpellPartKind.Element && p.IsAllowedBy(staff.AllowedTags)),
        SpellPartRegistry.All.First(p => p.Kind == SpellPartKind.Shape && p.IsAllowedBy(staff.AllowedTags)),
    ];

    /// <summary>An empty inventory with a new <paramref name="staffName"/> in hand, nothing bound.</summary>
    public static Inventory Holding(string staffName, out StaffItem staff, out ItemStack stack)
    {
        var inventory = Inventory.Empty();
        Hold(inventory, staffName, out staff, out stack);
        return inventory;
    }

    /// <summary>Adds a new <paramref name="staffName"/> to <paramref name="inventory"/> and takes it in hand, nothing bound.</summary>
    public static void Hold(Inventory inventory, string staffName, out StaffItem staff, out ItemStack stack)
    {
        staff = Staff(staffName);
        Assert.True(inventory.Add(staff.Id));
        var instance = inventory.Tools.Last().InstanceId;
        var slot = Enumerable.Range(0, Inventory.SlotCount).FirstOrDefault(i => inventory.ToolInstanceAt(i) == instance, -1);
        if (slot < 0)
        {
            slot = 0;
            Assert.True(inventory.EquipTool(slot, instance));
        }
        inventory.Select(slot);
        stack = inventory.GetToolStack(instance)!;
    }

    /// <summary>
    /// Binds <see cref="ValidRecipe"/> into <paramref name="slot"/> of the held staff and returns the stats a
    /// cast of it compiles to.
    /// </summary>
    public static SpellStats Bind(StaffItem staff, ItemStack stack, int slot, out SpellPart[] parts)
    {
        parts = ValidRecipe(staff);
        return Bind(staff, stack, slot, parts);
    }

    /// <summary>
    /// Binds <paramref name="parts"/> into <paramref name="slot"/> of the held staff and returns the stats a
    /// cast of it compiles to.
    /// </summary>
    public static SpellStats Bind(StaffItem staff, ItemStack stack, int slot, SpellPart[] parts)
    {
        Assert.True(StaffBinding.TryBind(staff, stack.Get<StaffComponent>(), slot, parts, out var bound, out _));
        stack.Set(bound);
        return new SpellRecipe(parts).Compile(staff.AllowedTags);
    }

    /// <summary>Teaches <paramref name="inventory"/> every registered spell part.</summary>
    public static void KnowAll(Inventory inventory) => inventory.LearnParts(SpellPartRegistry.All.Select(p => p.Key));

    /// <summary>A context for <paramref name="inventory"/> with a full mana pool.</summary>
    public static PlayerContext Player(Inventory inventory, bool creative = false) =>
        new(inventory) { Health = new PlayerHealth(), IsCreative = creative };
}
