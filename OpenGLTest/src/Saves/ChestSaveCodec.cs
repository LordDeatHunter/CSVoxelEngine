using System;
using System.Collections.Generic;
using VoxelEngine.Config;
using VoxelEngine.Simulation.Chests;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Saves;

/// <summary>
/// A chest's contents to and from the shape <c>stations.json</c> and the network snapshot share
/// (<see cref="StationSaveData.ChestData"/>): the slot count, then one entry per non-empty slot with its content by
/// <see cref="SaveNames"/> name and, for a tool or piece of gear, every component it carries (the same shape a saved
/// inventory instance uses). GL-free. A name that no longer resolves is dropped with one aggregated warning.
/// </summary>
public static class ChestSaveCodec
{
    /// <summary>The saved form of a chest at a cell.</summary>
    public static StationSaveData.ChestData Export(int x, int y, int z, ChestState chest)
    {
        var unnamed = new List<string>();
        var data = new StationSaveData.ChestData { Cell = [x, y, z], SlotCount = chest.SlotCount, Slots = [] };
        for (var i = 0; i < chest.SlotCount; i++)
        {
            var slot = chest[i];
            if (slot.IsEmpty) continue;
            if (slot.Instance != null)
            {
                if (InventorySaveCodec.ExportInstance(slot.Instance, unnamed) is { } tool)
                    data.Slots.Add(new StationSaveData.ChestSlotData { Slot = i, Name = tool.Name, Count = 1, Instance = tool });
                continue;
            }
            if (SaveNames.NameOf(slot.Id) is { } name)
                data.Slots.Add(new StationSaveData.ChestSlotData { Slot = i, Name = name, Count = slot.Count });
            else unnamed.Add($"id {slot.Id}");
        }
        if (unnamed.Count > 0)
            Log.Warn($"Chest save: left out content with no save name: {string.Join(", ", unnamed)}");
        return data;
    }

    /// <summary>
    /// The chest a saved entry describes, or null for an entry with no usable cell. Slot indices are bounded by the
    /// saved slot count (itself clamped to <see cref="ChestConfig.MaxSlots"/>), counts by
    /// <see cref="ChestConfig.MaxStack"/>, and content that no longer exists is skipped and named in
    /// <paramref name="dropped"/>.
    /// </summary>
    public static ChestState? Import(StationSaveData.ChestData data, List<string> dropped)
    {
        var slotCount = Math.Clamp(data.SlotCount ?? 0, 1, ChestConfig.MaxSlots);
        var chest = new ChestState(slotCount);
        if (data.Slots == null) return chest;
        foreach (var entry in data.Slots)
        {
            if (entry == null || entry.Slot is not { } index || index < 0 || index >= slotCount || !chest[index].IsEmpty)
                continue;
            if (entry.Instance != null)
            {
                var stack = InventorySaveCodec.ImportInstance(0, entry.Instance, dropped);
                if (stack != null && ChestTransfer.IsInstance(stack.DefinitionId))
                    chest.Set(index, new ChestSlot(stack.DefinitionId, 1, stack));
                else if (stack != null)
                    dropped.Add(entry.Name ?? "(no name)");
                continue;
            }
            if (!SaveNames.TryResolve(entry.Name, out var id) || ChestTransfer.IsInstance(id)
                || Inventory.CategoryOf(id) == null)
            {
                dropped.Add(entry.Name ?? "(no name)");
                continue;
            }
            var count = Math.Clamp(entry.Count ?? 0, 0, ChestConfig.MaxStack);
            if (count > 0) chest.Set(index, new ChestSlot(id, count, null));
        }
        return chest;
    }
}
