using System.Linq;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>
/// Puts a crucible into an <see cref="Inventory"/> <em>with its contents</em>. Taking a vessel
/// back out of a kiln has to preserve what is in it, and <see cref="Inventory.Add"/> only ever
/// hands out fresh empty tool instances.
///
/// <para>
/// This lives outside <see cref="Inventory"/> deliberately: the inventory knows nothing about
/// molten metal, and a crucible is the only item so far whose pickup carries state. It leans on
/// the one documented guarantee the tool pool gives — instance ids are handed out increasing and
/// never reused — to find the instance <see cref="Inventory.Add"/> just created.
/// </para>
/// </summary>
public static class CrucibleInventory
{
    /// <summary>
    /// Adds one crucible of <paramref name="crucibleId"/> and writes <paramref name="contents"/>
    /// onto the new instance. Returns that stack, or null when the tools pool is full — in which
    /// case nothing was added and the caller must keep the vessel where it was.
    /// </summary>
    public static ItemStack? Add(Inventory inventory, byte crucibleId, CrucibleComponent contents)
    {
        if (!inventory.Add(crucibleId)) return null;

        // Increasing, never-reused instance ids: the highest of this item id is the one just made
        var rows = inventory.ToolEntries().Where(r => r.ItemId == crucibleId).ToArray();
        if (rows.Length == 0) return null;
        var newest = rows.Max(r => r.InstanceId);

        var stack = inventory.GetToolStack(newest);
        stack?.Set(contents);
        return stack;
    }
}
