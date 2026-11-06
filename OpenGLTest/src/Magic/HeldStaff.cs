using System.Diagnostics.CodeAnalysis;
using VoxelEngine.Items;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Magic;

/// <summary>
/// The staff in an inventory's hand and its selected spell slot: the lookup every cast, the slot cycle
/// input, the server's slot selection and the HUD share. Main thread only, since an
/// <see cref="ItemStack"/>'s components live in a plain dictionary.
/// </summary>
internal static class HeldStaff
{
    /// <summary>The held staff and its live tool stack; false when the hand holds no staff.</summary>
    public static bool TryGet(Inventory inventory, [NotNullWhen(true)] out StaffItem? staff,
        [NotNullWhen(true)] out ItemStack? stack)
    {
        staff = inventory.HeldItem as StaffItem;
        stack = null;
        if (staff == null || !inventory.TryGetToolStack(inventory.SelectedToolInstance, out stack))
        {
            staff = null;
            return false;
        }
        return true;
    }

    /// <summary>The held staff's selected slot, or -1 without a staff in hand.</summary>
    public static int SelectedSlot(Inventory inventory) =>
        TryGet(inventory, out _, out var stack) ? stack.Get<StaffComponent>().Selected : -1;

    /// <summary>
    /// Selects <paramref name="slot"/> on the held staff. False, changing nothing, without a staff or for
    /// a slot the staff does not have (checked here, rather than clamped).
    /// </summary>
    public static bool TrySelect(Inventory inventory, int slot)
    {
        if (!TryGet(inventory, out var staff, out var stack) || slot < 0 || slot >= staff.Slots) return false;
        stack.Set(StaffBinding.Select(staff, stack.Get<StaffComponent>(), slot));
        return true;
    }

    /// <summary>
    /// Moves the held staff's selected slot by <paramref name="step"/>, wrapping both ways, and hands back
    /// the new selection. False with -1, changing nothing, without a staff or for a step of 0.
    /// </summary>
    public static bool TryCycle(Inventory inventory, int step, out int selected)
    {
        selected = -1;
        if (step == 0 || !TryGet(inventory, out var staff, out var stack)) return false;
        var cycled = StaffBinding.Cycle(staff, stack.Get<StaffComponent>(), step);
        stack.Set(cycled);
        selected = cycled.Selected;
        return true;
    }
}
