using System.Collections.Generic;
using System.IO;
using VoxelEngine.Saves;

namespace VoxelEngine.Simulation.Lanterns;

/// <summary>Name-based bounded lantern serialization. Unknown versions fail explicitly instead of erasing contents.</summary>
public static class LanternCodec
{
    public static LanternData Export(LanternState state) => new()
    {
        Remaining = state.Remaining, Capacity = state.Capacity,
        Storage = ChestSaveCodec.Export(0, 0, 0, state.Storage),
    };

    public static LanternState Import(LanternData data)
    {
        if (data.Version != 1) throw new InvalidDataException($"Unsupported lantern state version {data.Version}");
        if (!float.IsFinite(data.Remaining) || !float.IsFinite(data.Capacity) || data.Remaining < 0f
            || data.Capacity < data.Remaining || data.Capacity > 3600f)
            throw new InvalidDataException("Invalid lantern fuel");
        if (data.Storage != null && data.Storage.SlotCount != LanternConfig.Slots)
            throw new InvalidDataException("Invalid lantern storage size");
        var state = new LanternState { Remaining = data.Remaining, Capacity = data.Capacity };
        if (data.Storage == null) return state;
        var occupied = new HashSet<int>();
        if (data.Storage.Slots != null)
            foreach (var slot in data.Storage.Slots)
                if (slot == null || slot.Slot is not { } index || index < 0 || index >= LanternConfig.Slots
                    || !occupied.Add(index) || slot.Count is not (> 0 and <= ChestConfig.MaxStack) || slot.Instance != null
                    || !SaveNames.TryResolve(slot.Name, out var id) || !LanternState.Accepts(id))
                    throw new InvalidDataException("Invalid lantern storage entry");
        var dropped = new List<string>();
        var storage = ChestSaveCodec.Import(data.Storage, dropped)!;
        if (dropped.Count > 0) throw new InvalidDataException("Unrestorable lantern contents: " + string.Join(", ", dropped));
        for (var i = 0; i < storage.SlotCount; i++)
        {
            var slot = storage[i];
            if (!slot.IsEmpty && (!LanternState.Accepts(slot.Id) || slot.Instance != null))
                throw new InvalidDataException("Invalid lantern fuel item");
            state.Storage.Set(i, slot);
        }
        return state;
    }
}
