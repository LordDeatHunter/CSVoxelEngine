using System;
using VoxelEngine.Content;
using VoxelEngine.Simulation.Chests;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Simulation.Lanterns;

/// <summary>One lantern's fuel and eight storage slots. Only authoritative active lanterns advance.</summary>
public sealed class LanternState
{
    public ChestState Storage { get; } = new(LanternConfig.Slots);
    public float Remaining { get; internal set; }
    public float Capacity { get; internal set; }
    public bool Endless
    {
        get
        {
            for (var i = 0; i < Storage.SlotCount; i++)
                if (!Storage[i].IsEmpty && ContentRegistry.IsInTag("lantern_endless_fuel", Storage[i].Id)) return true;
            return false;
        }
    }
    public bool HasFuel
    {
        get
        {
            if (Remaining > 0 || Endless) return true;
            for (var i = 0; i < Storage.SlotCount; i++)
                if (!Storage[i].IsEmpty && Accepts(Storage[i].Id)) return true;
            return false;
        }
    }
    public float Fraction => Endless ? 1f : Capacity > 0 ? Math.Clamp(Remaining / Capacity, 0f, 1f) : 0f;
    public static bool IsLantern(ushort id) => BlockType.ById[id]?.Lantern == true;
    public static bool Accepts(ushort id) => ContentRegistry.IsInTag("lantern_fuel", id);
    public static LanternState? Of(ItemStack? stack) => stack?.Get<LanternComponent>().State;
    public static LanternState? Held(Inventory inventory) => Of(inventory.GetToolStack(inventory.SelectedToolInstance));

    /// <summary>Uses the existing coal fuel data; slime has one quarter of its duration.</summary>
    public static float Duration(ushort id)
    {
        var seconds = ContentRegistry.RequireItem("coal").FuelSeconds;
        return seconds * (ContentRegistry.IsInTag("lantern_quarter_fuel", id) ? LanternConfig.SlimeDurationShare : 1f);
    }

    /// <summary>Moves fuel through the ordinary conservation rules, refusing every untagged deposit.</summary>
    public int Move(Inventory inventory, in ChestMove move) => move.Kind switch
    {
        ChestMoveKind.DepositStack when move.Id > 0 && move.Id <= ushort.MaxValue && Accepts((ushort)move.Id) =>
            ChestTransfer.Deposit(inventory, Storage, (ushort)move.Id, move.Count <= 0 ? int.MaxValue : move.Count),
        ChestMoveKind.Withdraw => ChestTransfer.Withdraw(inventory, Storage, move.Slot, move.Count),
        ChestMoveKind.LootAll => ChestTransfer.LootAll(inventory, Storage),
        ChestMoveKind.QuickStack => QuickStack(inventory),
        _ => 0,
    };

    private int QuickStack(Inventory inventory)
    {
        var moved = 0;
        foreach (var row in inventory.StackEntries(InventoryCategory.Misc))
            if (Accepts(row.Id)) moved += ChestTransfer.Deposit(inventory, Storage, row.Id, int.MaxValue);
        return moved;
    }

    /// <summary>Consumes one stored unit only when the current burn is exhausted; exact boundaries spend no next unit.</summary>
    public void Advance(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds <= 0f || Endless) return;
        while (seconds > 0f)
        {
            if (Remaining <= 0f && !Refuel()) return;
            var used = Math.Min(seconds, Remaining);
            Remaining -= used;
            seconds -= used;
        }
    }

    private bool Refuel()
    {
        for (var i = 0; i < Storage.SlotCount; i++)
        {
            var slot = Storage[i];
            if (slot.IsEmpty || !Accepts(slot.Id)) continue;
            Capacity = Remaining = Duration(slot.Id);
            Storage.Set(i, slot.Count <= 1 ? ChestSlot.Empty : slot with { Count = slot.Count - 1 });
            return true;
        }
        return false;
    }

    public LanternState Clone()
    {
        var copy = new LanternState { Remaining = Remaining, Capacity = Capacity };
        for (var i = 0; i < Storage.SlotCount; i++) copy.Storage.Set(i, Storage[i]);
        return copy;
    }
}
