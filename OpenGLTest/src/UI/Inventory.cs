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
/// leave a refused drop on the ground (see <see cref="CanAccept"/>). The player spawns with
/// a test stock of 50 of every block plus one bucket.
/// </summary>
public class Inventory
{
    /// <summary>Hotbar slots along the bottom, switched with 1-10 / scroll.</summary>
    public const int SlotCount = 10;

    /// <summary>Shared pool caps: blocks and misc count every stacked unit, tools every instance.</summary>
    public const int BlockCapacity = 2048;
    public const int ToolCapacity = 8;
    public const int MiscCapacity = 2048;

    /// <summary>Test stock of every placeable block the player spawns with.</summary>
    public const int StartingStock = 50;

    /// <summary>Empty buckets the player spawns with; water is only reachable with one.</summary>
    public const int StartingBuckets = 1;

    /// <summary>Compasses in the misc pool at spawn; the player equips one to a hotbar
    /// slot through the [E] panel to turn the HUD compass on.</summary>
    public const int StartingCompasses = 1;

    // A pickup makes the slot's icon jump and settle back, echoing the count change
    // drawn in the slot's corner
    public const float PopSeconds = 0.25f;
    public const float PopScale = 1.3f;

    private readonly Dictionary<byte, int> _blocks = new();
    private readonly Dictionary<byte, int> _misc = new();

    // One entry per tool; InstanceId is handed out increasing and never reused, so a
    // hotbar slot can name its instance and dead references are detectable
    private readonly List<ToolEntry> _tools = [];
    private int _nextToolInstance = 1;

    private readonly HotbarSlot[] _hotbar = new HotbarSlot[SlotCount];

    // Seconds since each hotbar slot's last change, parked at PopSeconds once the pop finished
    private readonly float[] _popElapsed = FinishedPops();

    /// <summary>Which hotbar slot is in hand; always a valid index.</summary>
    public int SelectedIndex { get; private set; }

    /// <summary>Block or item id in the selected hotbar slot, or 0 when it is empty.</summary>
    public byte SelectedBlockId => IdAt(SelectedIndex);

    public int SelectedCount => CountAt(SelectedIndex);

    /// <summary>Tool instance in the selected slot, or 0 when it holds no live tool.</summary>
    public int SelectedToolInstance => ToolInstanceAt(SelectedIndex);

    public HotbarSlot SlotAt(int index) => _hotbar[Wrap(index)];

    /// <summary>
    /// Whether the selected slot holds a block that can actually be placed. Pure items
    /// (see <see cref="ItemType"/>) are collected but never placed.
    /// </summary>
    public bool CanPlaceSelected => SelectedBlockId != 0 && SelectedCount > 0 && !ItemType.IsItem(SelectedBlockId);

    /// <summary>Pool an id stores into, or null when it is not collectable (air, fluids,
    /// blocks without an item form, unknown ids).</summary>
    public static InventoryCategory? CategoryOf(byte id)
    {
        if (id == 0) return null;
        if (BlockType.ById[id] is { } block)
            return block is { HasItem: true, Fluid: false } ? InventoryCategory.Blocks : null;
        if (ItemType.IsItem(id))
            return ItemType.Types[id].UseAction != ItemUseAction.None
                ? InventoryCategory.Tools
                : InventoryCategory.Misc;
        return null;
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
    /// Credits picked-up blocks or items to their pool and pops every hotbar slot showing
    /// them. Tools arrive as new instances. Returns false for an id with no pool, or when
    /// its pool is full — the caller must leave the drop on the ground.
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
                    _tools.Add(new ToolEntry(_nextToolInstance++, id));
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
                PopSlotsShowing(id);
                if (fresh)
                    EquipNewStackable(id);
                return true;
            }
        }
    }

    /// <summary>
    /// Morphs a tool instance into another item (a bucket scooped full, a water bucket
    /// poured out) and pops every hotbar slot holding it, so shared slots flip together.
    /// </summary>
    public bool MorphTool(int instanceId, byte toId)
    {
        var index = _tools.FindIndex(t => t.InstanceId == instanceId);
        if (index < 0) return false;
        _tools[index] = _tools[index] with { ItemId = toId };
        for (var i = 0; i < _hotbar.Length; i++)
            if (_hotbar[i] is { IsTool: true } slot && slot.ToolInstance == instanceId)
                _popElapsed[i] = 0f;
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
        _popElapsed[i] = 0f;
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
            _popElapsed[i] = 0f;
            return true;
        }
        var alive = _tools.Any(t => t.InstanceId == instanceId);
        _hotbar[i] = alive ? new HotbarSlot(true, 0, instanceId) : HotbarSlot.Empty;
        _popElapsed[i] = 0f;
        return alive;
    }

    public void ClearSlot(int hotbarIndex) => _hotbar[Wrap(hotbarIndex)] = HotbarSlot.Empty;

    /// <summary>Id shown in a hotbar slot, or 0 when it is empty or its tool is gone.</summary>
    public byte IdAt(int index)
    {
        var slot = _hotbar[Wrap(index)];
        if (slot.IsTool)
            return _tools.FirstOrDefault(t => t.InstanceId == slot.ToolInstance).ItemId;
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
            return _tools.Any(t => t.InstanceId == slot.ToolInstance) ? 1 : 0;
        return CountFor(slot.Id);
    }

    public int ToolInstanceAt(int index)
    {
        var slot = _hotbar[Wrap(index)];
        if (!slot.IsTool) return 0;
        return _tools.Any(t => t.InstanceId == slot.ToolInstance) ? slot.ToolInstance : 0;
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

    /// <summary>Pool total for a stackable id, or the live-instance count for a tool id.</summary>
    public int CountFor(byte id)
    {
        var category = CategoryOf(id);
        return category switch
        {
            InventoryCategory.Blocks => _blocks.GetValueOrDefault(id),
            InventoryCategory.Misc => _misc.GetValueOrDefault(id),
            InventoryCategory.Tools => _tools.Count(t => t.ItemId == id),
            _ => 0,
        };
    }

    /// <summary>Recipes the pools can currently afford, including room for the result.</summary>
    public IReadOnlyList<CraftingRecipe> Craftable() =>
        ContentRegistry.CraftingRecipes.Where(CanCraft).ToArray();

    /// <summary>Whether one craft of this recipe would succeed: ingredients on hand plus
    /// room for the result (consumption frees room first, so a full pool still crafts
    /// when the ingredients come out of it).</summary>
    public bool CanCraft(CraftingRecipe recipe)
    {
        foreach (var ingredient in recipe.Ingredients)
            if (CountFor(ingredient.Id) < ingredient.Count)
                return false;

        var category = CategoryOf(recipe.ResultId);
        if (!category.HasValue) return false;
        return UsedOf(category.Value) - FreedBy(recipe, category.Value) + recipe.ResultCount
            <= CapacityOf(category.Value);
    }

    /// <summary>Consumes one craft's ingredients and adds its result. Returns false when
    /// the recipe is not currently craftable, consuming nothing.</summary>
    public bool TryCraft(CraftingRecipe recipe)
    {
        if (!CanCraft(recipe)) return false;
        foreach (var ingredient in recipe.Ingredients)
            RemoveUnits(ingredient.Id, ingredient.Count);
        return Add(recipe.ResultId, recipe.ResultCount);
    }

    /// <summary>Display name for a list row or preview heading.</summary>
    public static string DisplayName(byte id)
    {
        if (BlockType.ById[id] != null) return BlockType.ById[id]!.Name;
        if (ItemType.Types.TryGetValue(id, out var item)) return item.Name;
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
            .OrderBy(t => DisplayName(t.ItemId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.InstanceId)
            .Select(t => new ToolRow(t.InstanceId, t.ItemId, DisplayName(t.ItemId)))
            .ToArray();

    /// <summary>Item id a tool instance currently is, or 0 when it is gone.</summary>
    public byte ToolItemId(int instanceId) =>
        _tools.FirstOrDefault(t => t.InstanceId == instanceId).ItemId;

    /// <summary>Advances the slot pop animations.</summary>
    public void Update(float deltaTime)
    {
        for (var i = 0; i < _popElapsed.Length; i++)
            _popElapsed[i] = MathF.Min(PopSeconds, _popElapsed[i] + deltaTime);
    }

    /// <summary>Icon scale for a hotbar slot: 1 at rest, briefly larger just after a change.</summary>
    public float PopScaleAt(int index) =>
        MathUtils.Lerp(1f, PopScale, MathUtils.Pulse(_popElapsed[Wrap(index)] / PopSeconds));

    private Dictionary<byte, int>? StoreOf(byte id) => CategoryOf(id) switch
    {
        InventoryCategory.Blocks => _blocks,
        InventoryCategory.Misc => _misc,
        _ => null,
    };

    private int FreedBy(CraftingRecipe recipe, InventoryCategory category)
    {
        var freed = 0;
        foreach (var ingredient in recipe.Ingredients)
            if (CategoryOf(ingredient.Id) == category)
                freed += ingredient.Count;
        return freed;
    }

    private void RemoveUnits(byte id, int count)
    {
        if (CategoryOf(id) == InventoryCategory.Tools)
        {
            foreach (var instance in _tools.Where(t => t.ItemId == id).Take(count).ToArray())
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

    private void PopSlotsShowing(byte id)
    {
        for (var i = 0; i < _hotbar.Length; i++)
            if (IdAt(i) == id)
                _popElapsed[i] = 0f;
    }

    private void ClearSlotsShowing(byte id)
    {
        for (var i = 0; i < _hotbar.Length; i++)
            if (_hotbar[i] is { IsTool: false } slot && slot.Id == id)
                _hotbar[i] = HotbarSlot.Empty;
    }

    /// <summary>
    /// First hotbar slot showing nothing (empty or holding a long-gone tool), or -1 when
    /// the hotbar is full. New pickups equip there; the equip itself pops the slot.
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

    private static float[] FinishedPops()
    {
        var pops = new float[SlotCount];
        Array.Fill(pops, PopSeconds);
        return pops;
    }

    public Inventory()
    {
        // Test stock: 50 of every placeable block, so placing and the category caps can
        // be tried without mining first; one bucket, since water needs one to reach
        foreach (var type in BlockType.Types.Values)
            if (type.HasItem && !type.Fluid)
                _blocks[type.Id] = StartingStock;
        for (var i = 0; i < StartingBuckets; i++)
            if (ItemType.Types.Values.FirstOrDefault(t => t.UseAction == ItemUseAction.PickupFluid) is { } bucket)
                _tools.Add(new ToolEntry(_nextToolInstance++, bucket.Id));
        // A plain pickup like flint, found by display name the way the bucket is found
        // by behaviour. No use action: it only ever sits on the hotbar for the compass.
        if (ItemType.Types.Values.FirstOrDefault(t =>
                t.Name.Equals("Compass", StringComparison.OrdinalIgnoreCase)) is { } compass)
            _misc[compass.Id] = _misc.GetValueOrDefault(compass.Id) + StartingCompasses;
    }

    private sealed record ToolEntry(int InstanceId, byte ItemId);

    /// <summary>One stackable list row: icon, name and the pool count for that id.</summary>
    public readonly record struct StackRow(byte Id, string Name, int Count);

    /// <summary>One tool list row: icon, name and the instance it equips.</summary>
    public readonly record struct ToolRow(int InstanceId, byte ItemId, string Name);
}
