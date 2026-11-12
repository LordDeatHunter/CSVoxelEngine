using System;
using System.Collections.Generic;
using System.Text;
using VoxelEngine.Content.Spells;
using VoxelEngine.Items;
using VoxelEngine.Magic;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.UI.Hud;

/// <summary>
/// The HUD line beside the hotbar while a staff is in hand: which spell slot is selected and what is
/// bound in it (<c>Spell 2/3: Fire Bolt</c>, or <c>empty</c>). GL-free. The string is cached: it is
/// rebuilt only when the held tool instance, the selected slot or that slot's part keys change, compared
/// in an indexed loop against a copy of the last keys, so an unchanged frame costs a lookup and allocates
/// nothing. Main thread only, like the <see cref="ItemStack"/> it reads.
/// </summary>
public sealed class StaffSlotIndicator
{
    private int _instance = -1;
    private StaffItem? _staff;
    private int _selected = -1;
    private string[] _keys = Array.Empty<string>();
    private string? _label;

    /// <summary>The label for the staff in <paramref name="inventory"/>'s hand, or null without one.</summary>
    public string? Label(Inventory inventory)
    {
        if (!HeldStaff.TryGet(inventory, out var staff, out var stack)) return null;

        var component = stack.Get<StaffComponent>();
        var parts = component.PartsAt(component.Selected);
        if (_label != null && _instance == inventory.SelectedToolInstance && ReferenceEquals(_staff, staff)
            && _selected == component.Selected && SameKeys(parts))
            return _label;

        _instance = inventory.SelectedToolInstance;
        _staff = staff;
        _selected = component.Selected;
        _keys = new string[parts.Count];
        for (var i = 0; i < parts.Count; i++) _keys[i] = parts[i];
        _label = Build(staff, component.Selected, parts);
        return _label;
    }

    private bool SameKeys(IReadOnlyList<string> parts)
    {
        if (parts.Count != _keys.Length) return false;
        for (var i = 0; i < parts.Count; i++)
            if (!string.Equals(parts[i], _keys[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static string Build(StaffItem staff, int selected, IReadOnlyList<string> parts)
    {
        var text = new StringBuilder();
        text.Append("Spell ").Append(selected + 1).Append('/').Append(staff.Slots).Append(": ");
        if (parts.Count == 0) return text.Append("empty").ToString();
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) text.Append(' ');
            text.Append(SpellPartRegistry.TryGetByKey(parts[i])?.DisplayName ?? parts[i]);
        }
        return text.ToString();
    }
}
