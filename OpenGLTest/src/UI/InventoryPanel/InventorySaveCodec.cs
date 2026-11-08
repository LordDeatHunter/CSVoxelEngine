using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Spells;
using VoxelEngine.Items;
using VoxelEngine.Saves;
using VoxelEngine.Simulation.Waystones;

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
    /// crucible contents, a staff's bound spells (by part key) and the rename are written by
    /// <see cref="Inventory.Export"/>;
    /// <see cref="CooldownComponent"/> is handled by deliberate omission, since it holds an
    /// absolute window-clock time that means nothing in another session (a restored stack
    /// starts ready). The coverage test fails when a stack grows a component type not listed
    /// here, so a new one cannot be silently left out of the save.
    /// </summary>
    internal static readonly Type[] HandledComponentTypes =
    [
        typeof(DurabilityComponent), typeof(CrucibleComponent), typeof(DisplayNameComponent),
        typeof(CooldownComponent), typeof(StaffComponent), typeof(ModifiersComponent),
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
            if (ExportInstance(stack, unnamed) is { } tool)
                tools.Add(tool);

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
            // Always a list, empty or not: null is reserved for a save from before known parts
            KnownParts = inventory.KnownParts.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            Equipped = inventory.Equipment.Slots.Any(s => s != 0) ? inventory.Equipment.Slots.ToList() : null,
            Waystones = inventory.Waystones.Entries
                .Select(e => new PlayerSaveData.DiscoveredWaystoneData { Cell = [e.X, e.Y, e.Z], Name = e.Name })
                .ToList(),
        };
        if (unnamed.Count > 0)
            Log.Warn($"Player save: left out content with no save name: {string.Join(", ", unnamed)}");
        return data;
    }

    /// <summary>
    /// One tool or gear instance with every component it carries, by name (a chest slot, a saved tool). Null, with the
    /// reason noted in <paramref name="unnamed"/>, when its item has no stable save name.
    /// </summary>
    internal static PlayerSaveData.ToolData? ExportInstance(ItemStack stack, List<string> unnamed)
    {
        if (SaveNames.NameOf(stack.DefinitionId) is not { } name)
        {
            unnamed.Add($"tool id {stack.DefinitionId}");
            return null;
        }
        var tool = new PlayerSaveData.ToolData { InstanceId = stack.InstanceId, Name = name };
        if (stack.UsesDurability)
        {
            var durability = stack.Get<DurabilityComponent>();
            tool.UsesLeft = durability.UsesLeft;
            tool.MaxUses = durability.MaxUses;
        }
        if (stack.Item.CrucibleCapacityMl > 0)
            tool.Crucible = CrucibleSaveCodec.Export(stack.Get<CrucibleComponent>());
        if (stack.Item is StaffItem staff && stack.Get<StaffComponent>() is var spells && spells != default)
        {
            var slots = new List<List<string>?>(staff.Slots);
            for (var i = 0; i < staff.Slots; i++)
                slots.Add(spells.IsBound(i) ? spells.PartsAt(i).ToList() : null);
            tool.Staff = new PlayerSaveData.StaffData { Slots = slots, Selected = spells.Selected };
        }
        if (stack.Has<ModifiersComponent>())
            tool.Rolled = ExportRolled(stack.Get<ModifiersComponent>());
        tool.CustomName = stack.Get<DisplayNameComponent>().CustomName;
        if (stack.Enchantments.Count > 0)
            tool.Enchantments = new Dictionary<string, int>(stack.Enchantments, StringComparer.Ordinal);
        return tool;
    }

    /// <summary>
    /// Rebuilds one instance from its saved form as instance <paramref name="instanceId"/>: the item must still exist
    /// and be tool-like (a Tools, Misc or Gear item, never a block), and every component is restored through the same
    /// rules a player save uses. Null, with the reason in <paramref name="dropped"/>, when it cannot be restored.
    /// </summary>
    internal static ItemStack? ImportInstance(int instanceId, PlayerSaveData.ToolData tool, List<string> dropped)
    {
        // Any pure item may be a tool instance, not just a Tools-category one: the spawn stock puts the
        // copper pickaxe and axe (no durability, no use action, so their StorageCategory is Misc) straight into the
        // tools pool, and a save must bring them back. Blocks never are.
        if (!SaveNames.TryResolve(tool.Name, out var id)
            || ItemRegistry.TryGet(id) is not { } item
            || Inventory.CategoryOf(id) is not (InventoryCategory.Tools or InventoryCategory.Misc
                or InventoryCategory.Gear))
        {
            dropped.Add(tool.Name ?? "(no name)");
            return null;
        }

        var stack = new ItemStack(instanceId, item);
        if (item.MaxDurability > 0 && tool.UsesLeft is { } usesLeft)
            stack.Set(new DurabilityComponent(Math.Clamp(usesLeft, 0, item.MaxDurability),
                item.MaxDurability));
        if (item.CrucibleCapacityMl > 0 && tool.Crucible is { } crucible)
            stack.Set(CrucibleSaveCodec.Import(crucible, item.CrucibleCapacityMl, dropped));
        if (item is StaffItem staffItem && tool.Staff is { } staffData)
            RestoreStaff(stack, staffItem, staffData, tool.Name, dropped);
        if (item is GearItem && tool.Rolled is { } rolled)
            RestoreRolled(stack, rolled, tool.Name, dropped);
        if (tool.CustomName != null)
            stack.Rename(tool.CustomName);
        if (tool.Enchantments != null)
            foreach (var (enchantment, level) in tool.Enchantments)
                stack.Enchantments[enchantment] = level;
        return stack;
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

        // The known spell parts: the saved keys this run still has
        var knownParts = new List<string>();
        if (data.KnownParts != null)
            foreach (var key in data.KnownParts)
            {
                if (key == null) continue;
                if (SpellPartRegistry.TryGetByKey(key) != null) knownParts.Add(key);
                else dropped.Add($"spell part {key}");
            }
        inventory.ReplaceKnownParts(knownParts);
        inventory.Waystones.Replace(ImportWaystones(data.Waystones, dropped));

        ImportStore(inventory, data.Blocks, InventoryCategory.Blocks, inventory.Blocks, dropped);
        ImportStore(inventory, data.Misc, InventoryCategory.Misc, inventory.Misc, dropped);
        ImportTools(inventory, data, dropped);
        inventory.Equipment.Restore(data.Equipped);
        ImportHotbar(inventory, data);
        inventory.Select(data.SelectedIndex ?? 0);

        if (dropped.Count > 0)
            Log.Warn($"Player save: dropped inventory content this run cannot restore: {string.Join(", ", dropped)}");
    }

    // The discovered waystones: every entry with a cell and a name left after the rename rules (an edited file cannot
    // smuggle in a name a rename would refuse)
    private static List<DiscoveredWaystones.Entry> ImportWaystones(List<PlayerSaveData.DiscoveredWaystoneData>? saved,
        List<string> dropped)
    {
        var entries = new List<DiscoveredWaystones.Entry>();
        if (saved == null) return entries;
        foreach (var data in saved)
        {
            if (data?.Cell is not { Length: 3 } cell || !WaystoneNames.TrySanitize(data.Name, out var name))
            {
                dropped.Add($"waystone {data?.Name ?? "(no name)"}");
                continue;
            }
            entries.Add(new DiscoveredWaystones.Entry(cell[0], cell[1], cell[2], name));
        }
        return entries;
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
                if (tool == null) continue;
                if (!SaveNames.TryResolve(tool.Name, out var id) || ItemRegistry.TryGet(id) is not { } item)
                {
                    dropped.Add(tool.Name ?? "(no name)");
                    continue;
                }
                // Each pool keeps its own cap: a full tools pool never crowds out gear, nor the reverse
                var pool = item is GearItem ? InventoryCategory.Gear : InventoryCategory.Tools;
                if (inventory.UsedOf(pool) >= Inventory.CapacityOf(pool))
                {
                    dropped.Add($"{tool.Name} (pool full)");
                    continue;
                }
                if (tool.InstanceId is not { } instanceId || instanceId <= 0
                    || inventory.Tools.Any(t => t.InstanceId == instanceId))
                {
                    dropped.Add($"{tool.Name} (instance {tool.InstanceId?.ToString() ?? "none"})");
                    continue;
                }

                var stack = ImportInstance(instanceId, tool, dropped);
                if (stack == null) continue;
                inventory.Tools.Add(stack);
                maxRestored = Math.Max(maxRestored, instanceId);
            }
        inventory.NextToolInstance = Math.Max(data.NextToolInstance ?? 1, maxRestored + 1);
    }

    private static PlayerSaveData.RolledData ExportRolled(ModifiersComponent rolled) => new()
    {
        Rarity = rolled.Rarity.ToString(),
        Modifiers = rolled.Modifiers.Select(m => new PlayerSaveData.RolledModifierData
            { Stat = m.Stat.ToString(), Value = m.Value }).ToList(),
    };

    // A saved roll is trusted no further than the rules allow: the rarity and every stat must still exist, and a
    // value is clamped to what a roll of that stat can reach, so an edited file cannot mint an absurd piece
    private static void RestoreRolled(ItemStack stack, PlayerSaveData.RolledData data, string? name, List<string> dropped)
    {
        if (!Rarities.TryParse(data.Rarity, out var rarity))
        {
            dropped.Add($"{name} rarity {data.Rarity}");
            return;
        }
        var mods = new List<GearModifier>();
        if (data.Modifiers != null)
            foreach (var entry in data.Modifiers)
            {
                if (entry == null || !GearStats.TryParse(entry.Stat, out var stat) || !float.IsFinite(entry.Value))
                {
                    dropped.Add($"{name} modifier {entry?.Stat}");
                    continue;
                }
                mods.Add(new GearModifier(stat, Math.Clamp(entry.Value, -GearRoller.MaxModifierValue(stat),
                    GearRoller.MaxModifierValue(stat))));
            }
        stack.Set(new ModifiersComponent(rarity, mods.ToArray()));
    }

    // Re-binds each saved slot through the binding rules against this run's spell parts and the
    // staff's own tags, so a slot this run cannot bind (past the slot count, an unknown part, a
    // recipe now invalid) is dropped and named rather than restored as it was.
    private static void RestoreStaff(ItemStack stack, StaffItem staff, PlayerSaveData.StaffData data, string? name,
        List<string> dropped)
    {
        var spells = default(StaffComponent);
        if (data.Slots != null)
            for (var i = 0; i < data.Slots.Count; i++)
            {
                if (data.Slots[i] is not { Count: > 0 } keys) continue;
                if (i >= staff.Slots)
                {
                    dropped.Add($"{name} spell slot {i + 1}");
                    continue;
                }
                if (!StaffBinding.TryResolve(keys, out var parts, out var missing))
                {
                    dropped.Add($"spell part {missing}");
                    continue;
                }
                if (StaffBinding.TryBind(staff, spells, i, parts, out var bound, out var verdict)) spells = bound;
                else dropped.Add($"{name} spell slot {i + 1} ({verdict})");
            }

        spells = StaffBinding.Select(staff, spells, data.Selected ?? 0);
        if (spells != default) stack.Set(spells);
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
                if (slot.ToolInstance is { } instance
                    && inventory.Tools.Any(t => t.InstanceId == instance && t.Item is not GearItem))
                    inventory.Hotbar[i] = new HotbarSlot(true, 0, instance);
            }
            else if (SaveNames.TryResolve(slot.Name, out var id) && inventory.StoreOf(id) != null
                     && inventory.CountFor(id) > 0)
                inventory.Hotbar[i] = new HotbarSlot(false, id, 0);
        }
    }
}
