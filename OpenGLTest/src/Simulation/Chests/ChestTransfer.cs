using System;
using VoxelEngine.Config;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Simulation.Chests;

/// <summary>
/// The rules of moving things between a player's <see cref="Inventory"/> and a <see cref="ChestState"/>, GL-free and
/// the same for singleplayer and the server. A stackable (block or misc) moves by count and merges into slots holding
/// the same id up to <see cref="ChestConfig.MaxStack"/>; a tool or piece of gear moves whole, keeping its components,
/// one to a slot. A move never loses or duplicates anything: what does not fit stays where it was, and the return value
/// is how many units really moved.
/// </summary>
public static class ChestTransfer
{
    /// <summary>Whether an id moves as a single instance (a tool or gear piece) rather than by count.</summary>
    public static bool IsInstance(ushort id) =>
        Inventory.CategoryOf(id) is InventoryCategory.Tools or InventoryCategory.Gear;

    /// <summary>
    /// Moves up to <paramref name="count"/> units of a stackable from the inventory into the chest, merging into
    /// existing stacks first and then the empty slots. Returns how many went (0 for an id that is not a stackable the
    /// player holds, or a full chest).
    /// </summary>
    public static int Deposit(Inventory inventory, ChestState chest, ushort id, int count)
    {
        if (count <= 0 || IsInstance(id) || Inventory.CategoryOf(id) == null) return 0;
        var want = Math.Min(count, inventory.CountFor(id));
        var placed = Place(chest, id, want);
        return placed > 0 ? inventory.Remove(id, placed) : 0;
    }

    /// <summary>
    /// Moves one tool or piece of gear from the inventory into an empty chest slot, components and all (a worn piece
    /// comes off first). False when the player does not hold it or the chest is full.
    /// </summary>
    public static bool DepositInstance(Inventory inventory, ChestState chest, int instanceId)
    {
        var stack = inventory.GetToolStack(instanceId);
        if (stack == null) return false;
        var slot = chest.FirstEmpty();
        if (slot < 0) return false;
        chest.Set(slot, new ChestSlot(stack.DefinitionId, 1, stack.CloneAs(0)));
        inventory.RemoveTool(instanceId);
        return true;
    }

    /// <summary>
    /// Moves up to <paramref name="count"/> units (0 or less = the whole slot) out of a chest slot into the inventory,
    /// as far as the inventory has room. Returns how many went.
    /// </summary>
    public static int Withdraw(Inventory inventory, ChestState chest, int slotIndex, int count)
    {
        var slot = chest[slotIndex];
        if (slot.IsEmpty) return 0;

        if (slot.Instance != null)
        {
            if (!inventory.AddInstance(slot.Instance)) return 0;
            chest.Set(slotIndex, ChestSlot.Empty);
            return 1;
        }

        var want = count <= 0 ? slot.Count : Math.Min(count, slot.Count);
        var before = inventory.CountFor(slot.Id);
        inventory.Add(slot.Id, want);
        var moved = inventory.CountFor(slot.Id) - before;
        if (moved <= 0) return 0;
        chest.Set(slotIndex, slot.Count - moved <= 0 ? ChestSlot.Empty : slot with { Count = slot.Count - moved });
        return moved;
    }

    /// <summary>Takes every slot out of the chest as far as the inventory has room. Returns how many units moved.</summary>
    public static int LootAll(Inventory inventory, ChestState chest)
    {
        var moved = 0;
        for (var i = 0; i < chest.SlotCount; i++)
            moved += Withdraw(inventory, chest, i, 0);
        return moved;
    }

    /// <summary>
    /// Moves every stackable the player holds whose id the chest already holds into the chest, merging into its
    /// stacks (and empty slots when those are full). Tools and gear stay. Returns how many units moved.
    /// </summary>
    public static int QuickStack(Inventory inventory, ChestState chest)
    {
        var ids = new System.Collections.Generic.HashSet<ushort>();
        for (var i = 0; i < chest.SlotCount; i++)
            if (chest[i] is { IsEmpty: false, Instance: null } slot)
                ids.Add(slot.Id);
        var moved = 0;
        foreach (var id in ids)
            moved += Deposit(inventory, chest, id, int.MaxValue);
        return moved;
    }

    /// <summary>
    /// Puts up to <paramref name="count"/> units of a stackable into the chest (merging, then empty slots) and returns
    /// how many fit. Also what the loot materializer fills a ruin chest with.
    /// </summary>
    public static int Place(ChestState chest, ushort id, int count)
    {
        var left = count;
        for (var i = 0; i < chest.SlotCount && left > 0; i++)
        {
            var slot = chest[i];
            if (slot.IsEmpty || slot.Id != id || slot.Instance != null || slot.Count >= ChestConfig.MaxStack) continue;
            var add = Math.Min(left, ChestConfig.MaxStack - slot.Count);
            chest.Set(i, slot with { Count = slot.Count + add });
            left -= add;
        }
        for (var i = 0; i < chest.SlotCount && left > 0; i++)
        {
            if (!chest[i].IsEmpty) continue;
            var add = Math.Min(left, ChestConfig.MaxStack);
            chest.Set(i, new ChestSlot(id, add, null));
            left -= add;
        }
        return count - left;
    }
}
