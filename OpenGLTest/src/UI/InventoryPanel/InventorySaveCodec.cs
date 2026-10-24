using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Items;
using VoxelEngine.Saves;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// The save mapping of an <see cref="Inventory"/>: its pools, tool instances, hotbar and
/// selection to and from the <c>player.json</c> inventory shape. Static over the inventory
/// it is handed, whose pools it reads or refills in place; <see cref="Inventory.Export"/> and
/// <see cref="Inventory.Import"/> forward here.
/// </summary>
internal static class InventorySaveCodec
{
    /// <summary>
    /// Every <see cref="ItemStack"/> component type the save codec accounts for. Durability,
    /// crucible contents and the rename are written by <see cref="Inventory.Export"/>;
    /// <see cref="CooldownComponent"/> is handled by deliberate omission, since it holds an
    /// absolute window-clock time that means nothing in another session (a restored stack
    /// starts ready). The coverage test fails when a stack grows a component type not listed
    /// here, so a new one cannot be silently left out of the save.
    /// </summary>
    internal static readonly Type[] HandledComponentTypes =
    [
        typeof(DurabilityComponent), typeof(CrucibleComponent), typeof(DisplayNameComponent),
        typeof(CooldownComponent),
    ];

    /// <inheritdoc cref="Inventory.Export"/>
    internal static PlayerSaveData.InventoryData Export(Inventory inventory)
    {
        var unnamed = new List<string>();

        List<PlayerSaveData.StackData> ExportStore(Dictionary<byte, int> store)
        {
            var list = new List<PlayerSaveData.StackData>();
            foreach (var (id, count) in store)
            {
                if (count <= 0) continue;
                if (SaveNames.NameOf(id) is { } name)
                    list.Add(new PlayerSaveData.StackData { Name = name, Count = count });
                else unnamed.Add($"id {id}");
            }
            return list;
        }

        var tools = new List<PlayerSaveData.ToolData>();
        foreach (var stack in inventory.Tools)
        {
            if (SaveNames.NameOf(stack.DefinitionId) is not { } name)
            {
                unnamed.Add($"tool id {stack.DefinitionId}");
                continue;
            }
            var tool = new PlayerSaveData.ToolData { InstanceId = stack.InstanceId, Name = name };
            if (stack.UsesDurability)
            {
                var durability = stack.Get<DurabilityComponent>();
                tool.UsesLeft = durability.UsesLeft;
                tool.MaxUses = durability.MaxUses;
            }
            if (stack.Item.CrucibleCapacityMl > 0)
            {
                var crucible = stack.Get<CrucibleComponent>();
                tool.Crucible = new PlayerSaveData.CrucibleData
                {
                    Material = crucible.MaterialId == 0 ? null : SaveNames.MaterialNameOf(crucible.MaterialId),
                    MoltenMl = crucible.MoltenMl,
                    PendingMl = crucible.PendingMl,
                    PendingInput = SaveNames.NameOf(crucible.PendingInputId),
                };
            }
            tool.CustomName = stack.Get<DisplayNameComponent>().CustomName;
            if (stack.Enchantments.Count > 0)
                tool.Enchantments = new Dictionary<string, int>(stack.Enchantments, StringComparer.Ordinal);
            tools.Add(tool);
        }

        var hotbar = new List<PlayerSaveData.HotbarSlotData>(Inventory.SlotCount);
        foreach (var slot in inventory.Hotbar)
            hotbar.Add(slot.IsTool
                ? new PlayerSaveData.HotbarSlotData { IsTool = true, ToolInstance = slot.ToolInstance }
                : new PlayerSaveData.HotbarSlotData { IsTool = false, Name = SaveNames.NameOf(slot.Id), ToolInstance = 0 });

        var data = new PlayerSaveData.InventoryData
        {
            Blocks = ExportStore(inventory.Blocks),
            Misc = ExportStore(inventory.Misc),
            Tools = tools,
            Hotbar = hotbar,
            SelectedIndex = inventory.SelectedIndex,
            NextToolInstance = inventory.NextToolInstance,
            Known = ExportKnown(inventory),
        };
        if (unnamed.Count > 0)
            Log.Warn($"Player save: left out content with no save name: {string.Join(", ", unnamed)}");
        return data;
    }

    // The obtained set as save names, for Export
    private static List<string> ExportKnown(Inventory inventory)
    {
        var names = new List<string>(inventory.Known.Count);
        foreach (var id in inventory.Known)
            if (SaveNames.NameOf(id) is { } name) names.Add(name);
        return names;
    }

    /// <inheritdoc cref="Inventory.Import"/>
    internal static void Import(Inventory inventory, PlayerSaveData.InventoryData data)
    {
        inventory.Blocks.Clear();
        inventory.Misc.Clear();
        inventory.Tools.Clear();
        inventory.Known.Clear();
        Array.Fill(inventory.Hotbar, HotbarSlot.Empty);
        var dropped = new List<string>();

        // The obtained set: the saved names that still exist this run
        if (data.Known != null)
            foreach (var name in data.Known)
                if (name != null && SaveNames.TryResolve(name, out var known)) inventory.Known.Add(known);

        ImportStore(inventory, data.Blocks, InventoryCategory.Blocks, inventory.Blocks, dropped);
        ImportStore(inventory, data.Misc, InventoryCategory.Misc, inventory.Misc, dropped);
        ImportTools(inventory, data, dropped);
        ImportHotbar(inventory, data);
        inventory.Select(data.SelectedIndex ?? 0);

        if (dropped.Count > 0)
            Log.Warn($"Player save: dropped inventory content this run cannot restore: {string.Join(", ", dropped)}");
    }

    private static void ImportStore(Inventory inventory, List<PlayerSaveData.StackData>? entries,
        InventoryCategory category, Dictionary<byte, int> store, List<string> dropped)
    {
        if (entries == null) return;
        var capacity = Inventory.CapacityOf(category);
        var used = 0;
        foreach (var entry in entries)
        {
            if (entry == null) continue;
            if (!SaveNames.TryResolve(entry.Name, out var id) || Inventory.CategoryOf(id) != category)
            {
                dropped.Add(entry.Name ?? "(no name)");
                continue;
            }
            var count = Math.Min(entry.Count ?? 0, capacity - used);
            if (count <= 0) continue;
            store[id] = store.GetValueOrDefault(id) + count;
            used += count;
        }
    }

    private static void ImportTools(Inventory inventory, PlayerSaveData.InventoryData data, List<string> dropped)
    {
        var maxRestored = 0;
        if (data.Tools != null)
            foreach (var tool in data.Tools)
            {
                if (inventory.Tools.Count >= Inventory.ToolCapacity) break;
                if (tool == null) continue;
                // Any pure item may be a tool instance, not just a Tools-category one: the spawn
                // stock puts the copper pickaxe and axe (no durability, no use action, so their
                // StorageCategory is Misc) straight into the tools pool, and a save must bring
                // them back. Blocks never are.
                if (!SaveNames.TryResolve(tool.Name, out var id)
                    || ItemRegistry.TryGet(id) is not { } item
                    || Inventory.CategoryOf(id) is not (InventoryCategory.Tools or InventoryCategory.Misc))
                {
                    dropped.Add(tool.Name ?? "(no name)");
                    continue;
                }
                if (tool.InstanceId is not { } instanceId || instanceId <= 0
                    || inventory.Tools.Any(t => t.InstanceId == instanceId))
                {
                    dropped.Add($"{tool.Name} (instance {tool.InstanceId?.ToString() ?? "none"})");
                    continue;
                }

                var stack = new ItemStack(instanceId, item);
                if (item.MaxDurability > 0 && tool.UsesLeft is { } usesLeft)
                    stack.Set(new DurabilityComponent(Math.Clamp(usesLeft, 0, item.MaxDurability),
                        item.MaxDurability));
                if (item.CrucibleCapacityMl > 0 && tool.Crucible is { } crucible)
                    RestoreCrucible(stack, crucible, item.CrucibleCapacityMl, dropped);
                if (tool.CustomName != null)
                    stack.Rename(tool.CustomName);
                if (tool.Enchantments != null)
                    foreach (var (enchantment, level) in tool.Enchantments)
                        stack.Enchantments[enchantment] = level;
                inventory.Tools.Add(stack);
                maxRestored = Math.Max(maxRestored, instanceId);
            }
        inventory.NextToolInstance = Math.Max(data.NextToolInstance ?? 1, maxRestored + 1);
    }

    private static void RestoreCrucible(ItemStack stack, PlayerSaveData.CrucibleData crucible, int capacity,
        List<string> dropped)
    {
        byte material = 0;
        if (crucible.Material is { Length: > 0 } materialName
            && !SaveNames.TryResolveMaterial(materialName, out material))
        {
            dropped.Add($"molten {materialName}");
            return;
        }

        var pendingInput = SaveNames.TryResolve(crucible.PendingInput, out var input) ? input : (byte)0;
        var molten = Math.Clamp(crucible.MoltenMl ?? 0, 0, capacity);
        var pending = Math.Clamp(crucible.PendingMl ?? 0, 0, capacity - molten);
        stack.Set(new CrucibleComponent(material, molten, pending, pendingInput));
    }

    private static void ImportHotbar(Inventory inventory, PlayerSaveData.InventoryData data)
    {
        if (data.Hotbar == null) return;
        for (var i = 0; i < Math.Min(Inventory.SlotCount, data.Hotbar.Count); i++)
        {
            var slot = data.Hotbar[i];
            if (slot == null) continue;
            if (slot.IsTool == true)
            {
                if (slot.ToolInstance is { } instance && inventory.Tools.Any(t => t.InstanceId == instance))
                    inventory.Hotbar[i] = new HotbarSlot(true, 0, instance);
            }
            else if (SaveNames.TryResolve(slot.Name, out var id) && inventory.StoreOf(id) != null
                     && inventory.CountFor(id) > 0)
                inventory.Hotbar[i] = new HotbarSlot(false, id, 0);
        }
    }
}
