using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Spells;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Items;

/// <summary>
/// The rules for binding, clearing and selecting a staff's spell slots, pure and GL-free: each takes
/// the current <see cref="StaffComponent"/> and returns a new one, never changing the input. A slot
/// is bound only to a recipe that compiles <see cref="SpellVerdict.Valid"/> against the staff's own
/// tags, so a tier gates its parts by tag. <see cref="Magic.HeldStaff"/> (the spell table and the
/// server's inventory rules) calls it; a client only predicts a binding, the server's inventory
/// snapshot is the truth.
/// </summary>
public static class StaffBinding
{
    /// <summary>
    /// Binds <paramref name="parts"/> into <paramref name="slot"/>. True with the new component when
    /// the slot exists and the recipe compiles valid against <see cref="StaffItem.AllowedTags"/>;
    /// otherwise false with <paramref name="result"/> equal to <paramref name="current"/>.
    /// <paramref name="verdict"/> is the recipe's verdict either way.
    /// </summary>
    public static bool TryBind(StaffItem staff, in StaffComponent current, int slot, IReadOnlyList<SpellPart> parts,
        out StaffComponent result, out SpellVerdict verdict)
    {
        verdict = new SpellRecipe(parts).Compile(staff.AllowedTags).Verdict;
        result = current;
        if (slot < 0 || slot >= staff.Slots || verdict != SpellVerdict.Valid) return false;

        var slots = SlotsOf(staff, current);
        slots[slot] = parts.Select(p => p.Key).ToArray();
        result = new StaffComponent(slots, Math.Clamp(current.Selected, 0, staff.Slots - 1));
        return true;
    }

    /// <summary>A copy with <paramref name="slot"/> unbound; the input unchanged when the slot does not exist.</summary>
    public static StaffComponent Clear(StaffItem staff, in StaffComponent current, int slot)
    {
        if (slot < 0 || slot >= staff.Slots) return current;
        var slots = SlotsOf(staff, current);
        slots[slot] = null;
        return new StaffComponent(slots, current.Selected);
    }

    /// <summary>Moves the selected slot by <paramref name="step"/>, wrapping over the staff's slots both ways.</summary>
    public static StaffComponent Cycle(StaffItem staff, in StaffComponent current, int step)
    {
        var selected = ((current.Selected + step) % staff.Slots + staff.Slots) % staff.Slots;
        return new StaffComponent(SlotsOf(staff, current), selected);
    }

    /// <summary>Selects <paramref name="slot"/>, clamped to the staff's slots.</summary>
    public static StaffComponent Select(StaffItem staff, in StaffComponent current, int slot) =>
        new(SlotsOf(staff, current), Math.Clamp(slot, 0, staff.Slots - 1));

    /// <summary>
    /// Looks every part key up in <see cref="SpellPartRegistry"/>. False naming the first key this run
    /// does not know.
    /// </summary>
    public static bool TryResolve(IReadOnlyList<string> keys, out SpellPart[] parts, out string? missingKey)
    {
        parts = new SpellPart[keys.Count];
        missingKey = null;
        for (var i = 0; i < keys.Count; i++)
        {
            if (SpellPartRegistry.TryGetByKey(keys[i]) is not { } part)
            {
                missingKey = keys[i];
                parts = [];
                return false;
            }
            parts[i] = part;
        }
        return true;
    }

    // The component's slots as a list as long as the staff, for building a changed component
    private static IReadOnlyList<string>?[] SlotsOf(StaffItem staff, in StaffComponent current)
    {
        var slots = new IReadOnlyList<string>?[staff.Slots];
        for (var i = 0; i < slots.Length; i++)
            slots[i] = current.IsBound(i) ? current.PartsAt(i) : null;
        return slots;
    }
}
