using System;
using System.Collections.Generic;
using System.Linq;

namespace silkgltriangle;

/// <summary>Which storage pool an id belongs to. Each pool has its own shared cap.</summary>
public enum InventoryCategory
{
    Blocks,
    Tools,
    Misc,
    Crafting,
}

/// <summary>
/// One hotbar slot: either a reference to a stackable id (blocks/misc, whose count is the
/// pool total, so several slots can show the same id and stay in sync) or to a single tool
/// instance (tools are never stacked, so slots sharing an instance morph together while
/// slots holding different instances stay independent). The default value is empty.
/// </summary>
public readonly record struct HotbarSlot(bool IsTool, byte Id, int ToolInstance)
{
    public static HotbarSlot Empty => default;
    public bool IsEmpty => !IsTool && Id == 0;
}

/// <summary>
/// The player's belongings: three category pools plus a 10-slot hotbar of references into
/// them. Blocks and misc items stack per id against a shared per-category cap; tools are
/// individual instances (at most <see cref="ToolCapacity"/>) that morph in place, so a
/// bucket scooped in one slot becomes a water bucket in every slot holding that instance.
///
/// Pickups go through <see cref="Add"/>, which refuses what does not fit — the caller must
/// leave a refused drop on the ground (see <see cref="CanAccept"/>). A new survival player
/// spawns with nothing (<see cref="Empty"/>), and finds what to carry from the world; in
/// creative mode the panel browses every item and grants it on click. The test/dev constructor
/// stock (50 of every block plus a bucket, the copper tools, a compass, an obsidian blade, a
/// multiblock converter and a few station materials) is what tests and the item debugger open
/// with; a saved inventory replaces whatever the pool held wholesale through <see cref="Import"/>,
/// and <see cref="Export"/> is its save-side twin.
/// </summary>
public class Inventory
{
    /// <summary>Hotbar slots along the bottom, switched with 1-10 / scroll.</summary>
    public const int SlotCount = 10;

    /// <summary>Shared pool caps: blocks and misc count every stacked unit, tools every instance.</summary>
    public const int BlockCapacity = 2048;
    public const int ToolCapacity = 8;
    public const int MiscCapacity = 2048;

    /// <summary>Test stock of every placeable block the player spawns with. Every placeable block
    /// gets this much out of the one shared <see cref="BlockCapacity"/>, so it is kept small
    /// enough that the whole seed still leaves the pool room to add to as blocks are added.</summary>
    public const int StartingStock = 24;

    private readonly Dictionary<byte, int> _blocks = new();
    private readonly Dictionary<byte, int> _misc = new();

    // One entry per tool; InstanceId is handed out increasing and never reused, so a
    // hotbar slot can name its instance and dead references are detectable
    private readonly List<ItemStack> _tools = [];
    private int _nextToolInstance = 1;

    private readonly HotbarSlot[] _hotbar = new HotbarSlot[SlotCount];

    /// <summary>Which hotbar slot is in hand; always a valid index.</summary>
    public int SelectedIndex { get; private set; }

    /// <summary>Block or item id in the selected hotbar slot, or 0 when it is empty.</summary>
    public byte SelectedBlockId => IdAt(SelectedIndex);

    public int SelectedCount => CountAt(SelectedIndex);

    /// <summary>Tool instance in the selected slot, or 0 when it holds no live tool.</summary>
    public int SelectedToolInstance => ToolInstanceAt(SelectedIndex);

    /// <summary>The item in hand: the selected slot's item while it holds any, else null (a bare
    /// hand). What mining, attacks and uses read for the player holding this inventory.</summary>
    public Item? HeldItem => SelectedBlockId != 0 && SelectedCount > 0 ? ItemRegistry.TryGet(SelectedBlockId) : null;

    public HotbarSlot SlotAt(int index) => _hotbar[Wrap(index)];

    /// <summary>
    /// Whether the selected slot holds a block that can actually be placed. Pure items
    /// (see <see cref="ItemRegistry"/>) are collected but never placed.
    /// </summary>
    public bool CanPlaceSelected => SelectedBlockId != 0 && SelectedCount > 0 && !ItemRegistry.IsItem(SelectedBlockId);

    /// <summary>Pool an id stores into, or null when it is not collectable (air, fluids,
    /// blocks without an item form, unknown ids).</summary>
    public static InventoryCategory? CategoryOf(byte id)
    {
        if (id == 0) return null;
        if (BlockType.ById[id] is { } block)
            return block is { HasItem: true, Fluid: false } ? InventoryCategory.Blocks : null;
        return ItemRegistry.TryGet(id)?.StorageCategory;
    }

    public static int CapacityOf(InventoryCategory category) => category switch
    {
        InventoryCategory.Blocks => BlockCapacity,
        InventoryCategory.Tools => ToolCapacity,
        InventoryCategory.Misc => MiscCapacity,
        _ => 0,
    };

    public int UsedOf(InventoryCategory category) => category switch
    {
        InventoryCategory.Blocks => _blocks.Values.Sum(),
        InventoryCategory.Tools => _tools.Count,
        InventoryCategory.Misc => _misc.Values.Sum(),
        _ => 0,
    };

    /// <summary>Whether a pickup of this id would fit (one unit, or one instance for tools).</summary>
    public bool CanAccept(byte id)
    {
        var category = CategoryOf(id);
        return category.HasValue && UsedOf(category.Value) < CapacityOf(category.Value);
    }

    /// <summary>
    /// Credits picked-up blocks or items to their pool (the HUD pops every hotbar slot whose
    /// count rose, see <see cref="HotbarPickupPop"/>). Tools arrive as new instances.
    /// Returns false for an id with no pool, or when its pool is full — the caller must
    /// leave the drop on the ground.
    /// A pickup of an id the pools hold none of yet auto-equips into the first empty
    /// hotbar slot, when there is one, so a new find is immediately in hand.
    /// </summary>
    public bool Add(byte id, int amount = 1)
    {
        var category = CategoryOf(id);
        if (!category.HasValue || amount <= 0) return false;

        switch (category.Value)
        {
            case InventoryCategory.Tools:
                var room = ToolCapacity - _tools.Count;
                if (room <= 0) return false;
                var newTool = CountFor(id) == 0;
                var firstNewIndex = _tools.Count;
                for (var i = 0; i < Math.Min(room, amount); i++)
                    _tools.Add(new ItemStack(_nextToolInstance++, id));
                if (newTool)
                    EquipNewTool(_tools[firstNewIndex].InstanceId);
                return true;
            default:
            {
                var store = category.Value == InventoryCategory.Blocks ? _blocks : _misc;
                var capacity = CapacityOf(category.Value);
                var used = store.Values.Sum();
                if (used >= capacity) return false;
                var fresh = store.GetValueOrDefault(id) <= 0;
                var n = Math.Min(capacity - used, amount);
                store[id] = CountFor(id) + n;
                if (fresh)
                    EquipNewStackable(id);
                return true;
            }
        }
    }

    /// <summary>
    /// Morphs a tool instance into another item (a bucket scooped full, a water bucket
    /// poured out). Hotbar slots name the instance, so every slot holding it flips together.
    /// </summary>
    public bool MorphTool(int instanceId, byte toId)
    {
        var stack = _tools.FirstOrDefault(t => t.InstanceId == instanceId);
        if (stack == null) return false;
        var item = ItemRegistry.TryGet(toId);
        if (item == null) return false;
        stack.SetItem(item);
        return true;
    }

    /// <summary>
    /// Drops a tool instance from the pool and empties every hotbar slot holding it
    /// (thrown tools leave no ghost behind).
    /// </summary>
    public bool RemoveTool(int instanceId)
    {
        var index = _tools.FindIndex(t => t.InstanceId == instanceId);
        if (index < 0) return false;
        _tools.RemoveAt(index);
        for (var i = 0; i < _hotbar.Length; i++)
            if (_hotbar[i] is { IsTool: true } slot && slot.ToolInstance == instanceId)
                _hotbar[i] = HotbarSlot.Empty;
        return true;
    }

    /// <summary>
    /// Removes every unit or instance of an id and empties the hotbar slots that showed it.
    /// The creative catalog's clear button uses this; false when nothing was held.
    /// </summary>
    public bool RemoveAll(byte id)
    {
        if (CategoryOf(id) == InventoryCategory.Tools)
        {
            var instances = _tools.Where(t => t.DefinitionId == id)
                .Select(t => t.InstanceId)
                .ToArray();
            var removed = false;
            foreach (var instance in instances)
                removed |= RemoveTool(instance);
            return removed;
        }

        var store = StoreOf(id);
        if (store == null || !store.Remove(id)) return false;
        ClearSlotsShowing(id);
        return true;
    }

    /// <summary>Spends one unit from the selected slot. Tools are never spent — they morph.
    /// When the last unit goes, every hotbar slot showing it is cleared rather than left
    /// displaying a zero count.</summary>
    public bool ConsumeSelected()
    {
        var slot = _hotbar[SelectedIndex];
        if (slot.IsTool) return false;
        if (slot.Id == 0 || CountFor(slot.Id) <= 0) return false;

        var id = slot.Id;
        var store = StoreOf(id);
        if (store == null) return false;
        var left = store[id] - 1;
        if (left <= 0)
        {
            store.Remove(id);
            ClearSlotsShowing(id);
        }
        else store[id] = left;
        return true;
    }

    public void Select(int index) => SelectedIndex = Wrap(index);

    /// <summary>
    /// Moves the selection by a scroll wheel delta. Scrolling up (positive) walks left along
    /// the hotbar, matching the usual "previous item" feel, and the selection wraps.
    /// </summary>
    public void Scroll(float delta)
    {
        if (delta == 0f) return;
        Select(SelectedIndex - Math.Sign(delta));
    }

    /// <summary>
    /// Points a hotbar slot at a stackable id. Clicking the id the slot already shows
    /// clears it (toggle off); anything else overwrites.
    /// </summary>
    public void EquipStackable(int hotbarIndex, byte id)
    {
        var i = Wrap(hotbarIndex);
        _hotbar[i] = _hotbar[i] is { IsTool: false } slot && slot.Id == id && id != 0
            ? HotbarSlot.Empty
            : new HotbarSlot(false, id, 0);
    }

    /// <summary>
    /// Points a hotbar slot at one tool instance. Clicking the instance the slot already
    /// holds clears it. Returns false when the instance is gone (the slot is cleared).
    /// </summary>
    public bool EquipTool(int hotbarIndex, int instanceId)
    {
        var i = Wrap(hotbarIndex);
        if (_hotbar[i] is { IsTool: true } slot && slot.ToolInstance == instanceId)
        {
            _hotbar[i] = HotbarSlot.Empty;
            return true;
        }
        var alive = _tools.Any(t => t.InstanceId == instanceId);
        _hotbar[i] = alive ? new HotbarSlot(true, 0, instanceId) : HotbarSlot.Empty;
        return alive;
    }

    public void ClearSlot(int hotbarIndex) => _hotbar[Wrap(hotbarIndex)] = HotbarSlot.Empty;

    /// <summary>Id shown in a hotbar slot, or 0 when it is empty or its tool is gone.</summary>
    public byte IdAt(int index)
    {
        var slot = _hotbar[Wrap(index)];
        if (slot.IsTool)
            return LiveTool(slot.ToolInstance)?.DefinitionId ?? 0;
        return slot.Id;
    }

    /// <summary>
    /// Count shown in a hotbar slot: the pool total for stackables (shared across slots),
    /// 1 for a live tool, 0 for empty or orphaned slots.
    /// </summary>
    public int CountAt(int index)
    {
        var slot = _hotbar[Wrap(index)];
        if (slot.IsTool)
            return LiveTool(slot.ToolInstance) != null ? 1 : 0;
        return CountFor(slot.Id);
    }

    public int ToolInstanceAt(int index)
    {
        var slot = _hotbar[Wrap(index)];
        if (!slot.IsTool) return 0;
        return LiveTool(slot.ToolInstance) != null ? slot.ToolInstance : 0;
    }

    // The slot readers above run for every hotbar slot every frame (the hotbar draw, the HUD's
    // pickup pop), so they look the instance up with a plain loop rather than a capturing lambda.
    private ItemStack? LiveTool(int instanceId)
    {
        for (var i = 0; i < _tools.Count; i++)
            if (_tools[i].InstanceId == instanceId)
                return _tools[i];
        return null;
    }

    /// <summary>
    /// Whether any hotbar slot shows this id — equipped, whether selected or not. Used by
    /// the HUD compass, which is visible while its item sits anywhere on the hotbar.
    /// </summary>
    public bool ContainsInHotbar(byte id)
    {
        if (id == 0) return false;
        for (var i = 0; i < SlotCount; i++)
            if (IdAt(i) == id)
                return true;
        return false;
    }

    public bool PickBlock(byte id)
    {
        if (id == 0) return false;
        for (var i = 0; i < SlotCount; i++)
        {
            if (IdAt(i) != id) continue;
            Select(i);
            return true;
        }
        if (CountFor(id) <= 0) return false;
        if (CategoryOf(id) == InventoryCategory.Tools)
        {
            var instance = _tools.FirstOrDefault(t => t.DefinitionId == id);
            if (instance == null) return false;
            if (ToolInstanceAt(SelectedIndex) == instance.InstanceId) return true;
            EquipTool(SelectedIndex, instance.InstanceId);
            return true;
        }
        if (IdAt(SelectedIndex) == id) return true;
        EquipStackable(SelectedIndex, id);
        return true;
    }

    /// <summary>Pool total for a stackable id, or the live-instance count for a tool id.</summary>
    public int CountFor(byte id)
    {
        var category = CategoryOf(id);
        return category switch
        {
            InventoryCategory.Blocks => _blocks.GetValueOrDefault(id),
            InventoryCategory.Misc => _misc.GetValueOrDefault(id),
            InventoryCategory.Tools => _tools.Count(t => t.DefinitionId == id),
            _ => 0,
        };
    }

    /// <summary>Recipes the pools can currently afford, including room for the result.</summary>
    public IReadOnlyList<CraftingRecipe> Craftable() =>
        ContentRegistry.CraftingRecipes.Where(CanCraft).ToArray();

    /// <summary>Whether one craft of this recipe would succeed: ingredients on hand plus
    /// room for the result (consumption frees room first, so a full pool still crafts
    /// when the ingredients come out of it). Tag ingredients pool every member's stock.</summary>
    public bool CanCraft(CraftingRecipe recipe) => CanCraftBatch(recipe, 1, null);

    /// <summary>
    /// Whether <paramref name="batch"/> crafts would succeed. A null
    /// <paramref name="pickMember"/> pools every accepted id per ingredient; otherwise the
    /// picked member alone must cover its ingredient's share.
    /// </summary>
    public bool CanCraftBatch(CraftingRecipe recipe, int batch,
        Func<int, CraftingRecipe.Ingredient, byte>? pickMember)
    {
        if (batch < 1) return false;
        for (var i = 0; i < recipe.Ingredients.Length; i++)
        {
            var ingredient = recipe.Ingredients[i];
            var need = ingredient.Count * batch;
            if (pickMember != null)
            {
                var picked = pickMember(i, ingredient);
                if (!ingredient.Accepts(picked) || CountFor(picked) < need) return false;
            }
            else if (AvailableFor(ingredient) < need) return false;
        }

        var category = CategoryOf(recipe.ResultId);
        if (!category.HasValue) return false;
        return UsedOf(category.Value) - FreedBy(recipe, category.Value) * batch
            + recipe.ResultCount * batch <= CapacityOf(category.Value);
    }

    /// <summary>Consumes one craft's ingredients and adds its result. Returns false when
    /// the recipe is not currently craftable, consuming nothing.</summary>
    public bool TryCraft(CraftingRecipe recipe) => TryCraft(recipe, 1, null);

    /// <summary>
    /// Consumes <paramref name="batch"/> crafts and adds their results. A null
    /// <paramref name="pickMember"/> spends tag ingredients greedily across members;
    /// otherwise each ingredient comes out of its picked member only. Returns false when
    /// the batch is not currently craftable, consuming nothing.
    /// </summary>
    public bool TryCraft(CraftingRecipe recipe, int batch,
        Func<int, CraftingRecipe.Ingredient, byte>? pickMember)
    {
        if (!CanCraftBatch(recipe, batch, pickMember)) return false;
        for (var i = 0; i < recipe.Ingredients.Length; i++)
        {
            var ingredient = recipe.Ingredients[i];
            var need = ingredient.Count * batch;
            if (pickMember != null)
                RemoveUnits(pickMember(i, ingredient), need);
            else if (ingredient.IsTag)
            {
                foreach (var member in ingredient.AcceptedIds)
                {
                    if (need <= 0) break;
                    var take = Math.Min(need, CountFor(member));
                    if (take > 0)
                    {
                        RemoveUnits(member, take);
                        need -= take;
                    }
                }
            }
            else RemoveUnits(ingredient.Id, need);
        }
        return Add(recipe.ResultId, recipe.ResultCount * batch);
    }

    /// <summary>Display name for a list row or preview heading.</summary>
    public static string DisplayName(byte id)
    {
        if (BlockType.ById[id] != null) return BlockType.ById[id]!.DisplayName;
        if (ItemRegistry.TryGet(id) is { } item) return item.Name;
        return $"unknown {id}";
    }

    /// <summary>Stackable rows for a tab, holding something only (name-sorted).</summary>
    public IReadOnlyList<StackRow> StackEntries(InventoryCategory category)
    {
        if (category == InventoryCategory.Crafting || category == InventoryCategory.Tools)
            return [];
        var store = category == InventoryCategory.Blocks ? _blocks : _misc;
        return store
            .Where(kv => kv.Value > 0)
            .OrderBy(kv => DisplayName(kv.Key), StringComparer.OrdinalIgnoreCase)
            .Select(kv => new StackRow(kv.Key, DisplayName(kv.Key), kv.Value))
            .ToArray();
    }

    /// <summary>One row per tool instance (name-sorted, oldest first within a name).</summary>
    public IReadOnlyList<ToolRow> ToolEntries() =>
        _tools
            .OrderBy(t => DisplayName(t.DefinitionId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.InstanceId)
            .Select(t => new ToolRow(t.InstanceId, t.DefinitionId, DisplayName(t.DefinitionId)))
            .ToArray();

    /// <summary>Item id a tool instance currently is, or 0 when it is gone.</summary>
    public byte ToolItemId(int instanceId) =>
        _tools.FirstOrDefault(t => t.InstanceId == instanceId)?.DefinitionId ?? 0;

    /// <summary>Live tool stack for an instance id, or null when it is gone. Used by the item
    /// use dispatcher to hand the override its per-instance state.</summary>
    public ItemStack? GetToolStack(int instanceId) =>
        _tools.FirstOrDefault(t => t.InstanceId == instanceId);

    /// <summary>Durability left on a tool instance, or null when it has none / is gone.</summary>
    public int? ToolDurabilityLeft(int instanceId)
    {
        var stack = _tools.FirstOrDefault(t => t.InstanceId == instanceId);
        if (stack == null || !stack.UsesDurability) return null;
        return stack.Get<DurabilityComponent>().UsesLeft;
    }

    /// <summary>Durability fraction 0..1 for the bar, or null when endless / gone.</summary>
    public float? ToolDurabilityFraction(int instanceId)
    {
        var stack = _tools.FirstOrDefault(t => t.InstanceId == instanceId);
        if (stack == null || !stack.UsesDurability) return null;
        return stack.DurabilityFraction;
    }

    /// <summary>Spends durability on a tool instance, removing it when it breaks. Returns true when it broke.</summary>
    public bool DamageTool(int instanceId, int amount = 1)
    {
        var stack = _tools.FirstOrDefault(t => t.InstanceId == instanceId);
        if (stack == null) return false;
        if (!stack.UsesDurability) return false;
        if (!stack.Damage(amount)) return false;
        RemoveTool(instanceId);
        return true;
    }

    /// <summary>Whether a tool instance's use delay has elapsed at this scene time.</summary>
    public bool ToolReadyAt(int instanceId, double now)
    {
        var stack = _tools.FirstOrDefault(t => t.InstanceId == instanceId);
        return stack != null && stack.CanUseAt(now);
    }

    /// <summary>Starts a tool instance's use-delay cooldown at this scene time.</summary>
    public void MarkToolUsed(int instanceId, double now, float delay) =>
        _tools.FirstOrDefault(t => t.InstanceId == instanceId)?.MarkUsed(now, delay);

    /// <summary>
    /// Every <see cref="ItemStack"/> component type the save codec accounts for. Durability,
    /// crucible contents and the rename are written by <see cref="Export"/>;
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

    /// <summary>
    /// Snapshots the whole inventory for <c>player.json</c>: every pool entry, every tool
    /// instance with its components, the hotbar layout and the selection. Content is named
    /// through <see cref="SaveNames"/>; an id with no stable name is left out with one warning.
    /// </summary>
    public PlayerSaveData.InventoryData Export()
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
        foreach (var stack in _tools)
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

        var hotbar = new List<PlayerSaveData.HotbarSlotData>(SlotCount);
        foreach (var slot in _hotbar)
            hotbar.Add(slot.IsTool
                ? new PlayerSaveData.HotbarSlotData { IsTool = true, ToolInstance = slot.ToolInstance }
                : new PlayerSaveData.HotbarSlotData { IsTool = false, Name = SaveNames.NameOf(slot.Id), ToolInstance = 0 });

        var data = new PlayerSaveData.InventoryData
        {
            Blocks = ExportStore(_blocks),
            Misc = ExportStore(_misc),
            Tools = tools,
            Hotbar = hotbar,
            SelectedIndex = SelectedIndex,
            NextToolInstance = _nextToolInstance,
        };
        if (unnamed.Count > 0)
            Log.Warn($"Player save: left out content with no save name: {string.Join(", ", unnamed)}");
        return data;
    }

    /// <summary>
    /// Replaces everything this inventory holds — the constructor's spawn stock included —
    /// with a saved one (main thread). Pool caps are honoured, tool instance ids are kept (a
    /// repeated id is restored once) and later pickups get ids above every restored one; a
    /// durability or crucible value is clamped to this run's maximum. Names this run does not
    /// know, or that belong to the wrong pool, are dropped with one warning, and a hotbar slot
    /// pointing at dropped content comes back empty.
    /// </summary>
    public void Import(PlayerSaveData.InventoryData data)
    {
        _blocks.Clear();
        _misc.Clear();
        _tools.Clear();
        Array.Fill(_hotbar, HotbarSlot.Empty);
        var dropped = new List<string>();

        void ImportStore(List<PlayerSaveData.StackData>? entries, InventoryCategory category,
            Dictionary<byte, int> store)
        {
            if (entries == null) return;
            var capacity = CapacityOf(category);
            var used = 0;
            foreach (var entry in entries)
            {
                if (entry == null) continue;
                if (!SaveNames.TryResolve(entry.Name, out var id) || CategoryOf(id) != category)
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

        ImportStore(data.Blocks, InventoryCategory.Blocks, _blocks);
        ImportStore(data.Misc, InventoryCategory.Misc, _misc);

        var maxRestored = 0;
        if (data.Tools != null)
            foreach (var tool in data.Tools)
            {
                if (_tools.Count >= ToolCapacity) break;
                if (tool == null) continue;
                // Any pure item may be a tool instance, not just a Tools-category one: the spawn
                // stock puts the copper pickaxe and axe (no durability, no use action, so their
                // StorageCategory is Misc) straight into the tools pool, and a save must bring
                // them back. Blocks never are.
                if (!SaveNames.TryResolve(tool.Name, out var id)
                    || ItemRegistry.TryGet(id) is not { } item
                    || CategoryOf(id) is not (InventoryCategory.Tools or InventoryCategory.Misc))
                {
                    dropped.Add(tool.Name ?? "(no name)");
                    continue;
                }
                if (tool.InstanceId is not { } instanceId || instanceId <= 0
                    || _tools.Any(t => t.InstanceId == instanceId))
                {
                    dropped.Add($"{tool.Name} (instance {tool.InstanceId?.ToString() ?? "none"})");
                    continue;
                }

                var stack = new ItemStack(instanceId, item);
                if (item.MaxDurability > 0 && tool.UsesLeft is { } usesLeft)
                    stack.Set(new DurabilityComponent(Math.Clamp(usesLeft, 0, item.MaxDurability),
                        item.MaxDurability));
                if (item.CrucibleCapacityMl > 0 && tool.Crucible is { } crucible)
                {
                    byte material = 0;
                    if (!string.IsNullOrEmpty(crucible.Material)
                        && !SaveNames.TryResolveMaterial(crucible.Material, out material))
                        dropped.Add($"molten {crucible.Material}");
                    else
                    {
                        var pendingInput = SaveNames.TryResolve(crucible.PendingInput, out var input) ? input : (byte)0;
                        var capacity = item.CrucibleCapacityMl;
                        var molten = Math.Clamp(crucible.MoltenMl ?? 0, 0, capacity);
                        var pending = Math.Clamp(crucible.PendingMl ?? 0, 0, capacity - molten);
                        stack.Set(new CrucibleComponent(material, molten, pending, pendingInput));
                    }
                }
                if (tool.CustomName != null)
                    stack.Rename(tool.CustomName);
                if (tool.Enchantments != null)
                    foreach (var (enchantment, level) in tool.Enchantments)
                        stack.Enchantments[enchantment] = level;
                _tools.Add(stack);
                maxRestored = Math.Max(maxRestored, instanceId);
            }
        _nextToolInstance = Math.Max(data.NextToolInstance ?? 1, maxRestored + 1);

        if (data.Hotbar != null)
            for (var i = 0; i < Math.Min(SlotCount, data.Hotbar.Count); i++)
            {
                var slot = data.Hotbar[i];
                if (slot == null) continue;
                if (slot.IsTool == true)
                {
                    if (slot.ToolInstance is { } instance && _tools.Any(t => t.InstanceId == instance))
                        _hotbar[i] = new HotbarSlot(true, 0, instance);
                }
                else if (SaveNames.TryResolve(slot.Name, out var id) && StoreOf(id) != null && CountFor(id) > 0)
                    _hotbar[i] = new HotbarSlot(false, id, 0);
            }
        Select(data.SelectedIndex ?? 0);

        if (dropped.Count > 0)
            Log.Warn($"Player save: dropped inventory content this run cannot restore: {string.Join(", ", dropped)}");
    }

    private Dictionary<byte, int>? StoreOf(byte id) => CategoryOf(id) switch
    {
        InventoryCategory.Blocks => _blocks,
        InventoryCategory.Misc => _misc,
        _ => null,
    };

    private int AvailableFor(CraftingRecipe.Ingredient ingredient)
    {
        var total = 0;
        foreach (var id in ingredient.AcceptedIds)
            total += CountFor(id);
        return total;
    }

    private int FreedBy(CraftingRecipe recipe, InventoryCategory category)
    {
        var freed = 0;
        foreach (var ingredient in recipe.Ingredients)
        {
            if (ingredient.IsTag)
            {
                if (ingredient.AcceptedIds.All(id => CategoryOf(id) == category))
                    freed += ingredient.Count;
            }
            else if (CategoryOf(ingredient.Id) == category) freed += ingredient.Count;
        }
        return freed;
    }

    private void RemoveUnits(byte id, int count)
    {
        if (CategoryOf(id) == InventoryCategory.Tools)
        {
            foreach (var instance in _tools.Where(t => t.DefinitionId == id).Take(count).ToArray())
                RemoveTool(instance.InstanceId);
            return;
        }
        var store = StoreOf(id);
        if (store == null) return;
        var left = store.GetValueOrDefault(id) - count;
        if (left <= 0)
        {
            store.Remove(id);
            ClearSlotsShowing(id);
        }
        else store[id] = left;
    }

    private void ClearSlotsShowing(byte id)
    {
        for (var i = 0; i < _hotbar.Length; i++)
            if (_hotbar[i] is { IsTool: false } slot && slot.Id == id)
                _hotbar[i] = HotbarSlot.Empty;
    }

    /// <summary>
    /// First hotbar slot showing nothing (empty or holding a long-gone tool), or -1 when
    /// the hotbar is full. New pickups equip there.
    /// </summary>
    private int FirstEmptySlot()
    {
        for (var i = 0; i < _hotbar.Length; i++)
            if (IdAt(i) == 0)
                return i;
        return -1;
    }

    private void EquipNewStackable(byte id)
    {
        var slot = FirstEmptySlot();
        if (slot >= 0)
            EquipStackable(slot, id);
    }

    private void EquipNewTool(int instanceId)
    {
        var slot = FirstEmptySlot();
        if (slot >= 0)
            EquipTool(slot, instanceId);
    }

    private static int Wrap(int index) => ((index % SlotCount) + SlotCount) % SlotCount;

    /// <summary>
    /// The debug/test inventory: 50 of every placeable block plus the handful of starting tools
    /// and materials below. A new survival player owns none of it — <see cref="World"/> builds
    /// an <see cref="Empty"/> pool — so this stock is only what tests and the item debugger open
    /// with, and the pools it fills are the ones the category-cap tests measure against.
    /// </summary>
    public Inventory() : this(seedTestStock: true)
    {
    }

    /// <summary>A stockless pool, which is what a new survival player owns.</summary>
    public static Inventory Empty() => new(seedTestStock: false);

    private Inventory(bool seedTestStock)
    {
        if (!seedTestStock) return;
        foreach (var type in BlockType.Types.Values)
            if (type.HasItem && !type.Fluid)
                _blocks[type.Id] = StartingStock;
        // One bucket, since water needs one to reach, plus the axe, found by behaviour and
        // display name the way the compass below is.
        if (ItemRegistry.FindByAction(ItemUseAction.PickupFluid) is { } bucket)
            _tools.Add(new ItemStack(_nextToolInstance++, bucket));
        if (ItemRegistry.FindByName("Copper Pickaxe") is { } pickaxe)
            _tools.Add(new ItemStack(_nextToolInstance++, pickaxe));
        if (ItemRegistry.FindByName("Copper Axe") is { } axe)
            _tools.Add(new ItemStack(_nextToolInstance++, axe));
        // A plain pickup like flint, found by display name. No use action: it only ever
        // sits on the hotbar for the compass.
        if (ItemRegistry.FindByName("Compass") is { } compass)
            _misc[compass.Id] = _misc.GetValueOrDefault(compass.Id) + 1;
        if (ItemRegistry.FindByName("Obsidian Blade") is { } obsidianBlade)
            _misc[obsidianBlade.Id] = _misc.GetValueOrDefault(obsidianBlade.Id) + 1;
        if (ItemRegistry.FindByName("Multiblock Converter") is { } converter)
            _misc[converter.Id] = _misc.GetValueOrDefault(converter.Id) + 1;
        if (ItemRegistry.FindByName("Mud Brick") is { } brick)
            _misc[brick.Id] = _misc.GetValueOrDefault(brick.Id) + 50;
        if (ItemRegistry.FindByName("Coal") is { } coal)
            _misc[coal.Id] = _misc.GetValueOrDefault(coal.Id) + 50;
        if (ItemRegistry.FindByName("Crushed Copper") is { } crushedCopper)
            _misc[crushedCopper.Id] = _misc.GetValueOrDefault(crushedCopper.Id) + 50;
    }

    /// <summary>One stackable list row: icon, name and the pool count for that id.</summary>
    public readonly record struct StackRow(byte Id, string Name, int Count);

    /// <summary>One tool list row: icon, name and the instance it equips.</summary>
    public readonly record struct ToolRow(int InstanceId, byte ItemId, string Name);
}
