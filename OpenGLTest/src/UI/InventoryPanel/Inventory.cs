using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Config;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Items;
using VoxelEngine.Saves;
using VoxelEngine.Simulation.Waystones;
using VoxelEngine.UI.Hud;

namespace VoxelEngine.UI.InventoryPanel;

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

    /// <summary>Gear instances a player carries (see <see cref="GearConfig.GearCapacity"/>); worn pieces count.</summary>
    public const int GearCapacity = GearConfig.GearCapacity;

    /// <summary>Test stock of every placeable block the player spawns with. Every placeable block
    /// gets this much out of the one shared <see cref="BlockCapacity"/>, so it is kept small
    /// enough that the whole seed still leaves the pool room to add to as blocks are added.</summary>
    public const int StartingStock = 20;

    private readonly Dictionary<ushort, int> _blocks = new();
    // The ids this player has obtained (the look-at label's "Unknown" rule); filled by Add and
    // saved as names like every other pool
    private readonly HashSet<ushort> _known = [];
    // The spell part keys (<kind>/<name>) this player knows; saved by key in player.json and synced in
    // the inventory snapshot
    private readonly HashSet<string> _knownParts = new(StringComparer.Ordinal);
    private readonly Dictionary<ushort, int> _misc = new();

    // One entry per tool; InstanceId is handed out increasing and never reused, so a
    // hotbar slot can name its instance and dead references are detectable
    private readonly List<ItemStack> _tools = [];
    private int _nextToolInstance = 1;

    private readonly HotbarSlot[] _hotbar = new HotbarSlot[SlotCount];

    /// <summary>What the player wears; the worn pieces are instances in the Gear pool.</summary>
    public Equipment Equipment { get; }

    /// <summary>The waystones this player has discovered; saved in <c>player.json</c> and synced in the inventory
    /// snapshot like the known spell parts.</summary>
    public DiscoveredWaystones Waystones { get; } = new();

    // The same instances, handed to the static helpers that work on this inventory
    // (InventoryCrafting, InventorySaveCodec); state lives only here
    internal List<ItemStack> Tools => _tools;
    internal Dictionary<ushort, int> Blocks => _blocks;
    internal Dictionary<ushort, int> Misc => _misc;
    internal HashSet<ushort> Known => _known;
    internal HotbarSlot[] Hotbar => _hotbar;
    internal int NextToolInstance { get => _nextToolInstance; set => _nextToolInstance = value; }

    /// <summary>Which hotbar slot is in hand; always a valid index.</summary>
    public int SelectedIndex { get; private set; }

    /// <summary>Block or item id in the selected hotbar slot, or 0 when it is empty.</summary>
    public ushort SelectedBlockId => IdAt(SelectedIndex);

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
    public static InventoryCategory? CategoryOf(ushort id)
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
        InventoryCategory.Gear => GearCapacity,
        _ => 0,
    };

    public int UsedOf(InventoryCategory category) => category switch
    {
        InventoryCategory.Blocks => _blocks.Values.Sum(),
        InventoryCategory.Tools => _tools.Count(t => t.Item is not GearItem),
        InventoryCategory.Misc => _misc.Values.Sum(),
        InventoryCategory.Gear => _tools.Count(t => t.Item is GearItem),
        _ => 0,
    };

    /// <summary>Whether a pickup of this id would fit (one unit, or one instance for tools).</summary>
    public bool CanAccept(ushort id)
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
    /// hotbar slot, when there is one, so a new find is immediately in hand; a grant the player
    /// clicked in the panel passes <paramref name="autoEquip"/> false, since only a slot click or a
    /// drop puts it on the hotbar.
    /// </summary>
    public bool Add(ushort id, int amount = 1, bool autoEquip = true)
    {
        var category = CategoryOf(id);
        if (!category.HasValue || amount <= 0) return false;
        _known.Add(id);

        switch (category.Value)
        {
            case InventoryCategory.Tools:
            case InventoryCategory.Gear:
                var room = CapacityOf(category.Value) - UsedOf(category.Value);
                if (room <= 0) return false;
                var newTool = CountFor(id) == 0;
                var firstNewIndex = _tools.Count;
                for (var i = 0; i < Math.Min(room, amount); i++)
                    _tools.Add(new ItemStack(_nextToolInstance++, id));
                if (newTool && autoEquip && category.Value == InventoryCategory.Tools)
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
                if (fresh && autoEquip)
                    EquipNewStackable(id);
                return true;
            }
        }
    }

    /// <summary>
    /// Removes up to <paramref name="count"/> units of a stackable (block or misc) id from its pool and returns how many
    /// went, clearing the hotbar slots that showed it when the last unit goes. 0 for an id with no stackable pool.
    /// </summary>
    public int Remove(ushort id, int count)
    {
        var store = StoreOf(id);
        if (store == null || count <= 0) return 0;
        var have = store.GetValueOrDefault(id);
        var taken = Math.Min(have, count);
        if (taken <= 0) return 0;
        if (taken == have)
        {
            store.Remove(id);
            ClearSlotsShowing(id);
        }
        else store[id] = have - taken;
        return taken;
    }

    /// <summary>
    /// Takes in a tool or piece of gear that lives elsewhere (a chest slot) as a new instance of this inventory, keeping
    /// every component it carries. False, changing nothing, when its pool is full or it is not an instance item.
    /// </summary>
    public bool AddInstance(ItemStack template)
    {
        var category = CategoryOf(template.DefinitionId);
        if (category is not (InventoryCategory.Tools or InventoryCategory.Gear)
            || UsedOf(category.Value) >= CapacityOf(category.Value))
            return false;
        _known.Add(template.DefinitionId);
        _tools.Add(template.CloneAs(_nextToolInstance++));
        return true;
    }

    /// <summary>
    /// Credits one piece of loot gear that rolled <paramref name="rolled"/> (see <see cref="GearRoller"/>): a new
    /// gear instance carrying its rarity and modifiers and the name they earned. False for a non-gear id or a full
    /// gear pool, in which case nothing is added.
    /// </summary>
    public bool AddRolledGear(ushort id, in RolledGear rolled)
    {
        if (ItemRegistry.TryGet(id) is not GearItem gear || !Add(id)) return false;
        var stack = _tools[^1];
        stack.Set(rolled.Modifiers);
        if (rolled.Name != gear.Name) stack.Rename(rolled.Name);
        return true;
    }

    /// <summary>
    /// Morphs a tool instance into another item (a bucket scooped full, a water bucket
    /// poured out). Hotbar slots name the instance, so every slot holding it flips together.
    /// </summary>
    public bool MorphTool(int instanceId, ushort toId)
    {
        var stack = _tools.FirstOrDefault(t => t.InstanceId == instanceId);
        if (stack == null) return false;
        var item = ItemRegistry.TryGet(toId);
        if (item == null) return false;
        stack.SetItem(item);
        return true;
    }

    /// <summary>Hotbar slot holding a tool instance, or -1 when no slot does (the tool broke).</summary>
    public int SlotOfTool(int instanceId)
    {
        for (var i = 0; i < _hotbar.Length; i++)
            if (_hotbar[i] is { IsTool: true } slot && slot.ToolInstance == instanceId)
                return i;
        return -1;
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
        Equipment.Forget(instanceId);
        for (var i = 0; i < _hotbar.Length; i++)
            if (_hotbar[i] is { IsTool: true } slot && slot.ToolInstance == instanceId)
                _hotbar[i] = HotbarSlot.Empty;
        return true;
    }

    /// <summary>
    /// Removes every unit or instance of an id and empties the hotbar slots that showed it.
    /// The creative catalog's clear button uses this; false when nothing was held.
    /// </summary>
    public bool RemoveAll(ushort id)
    {
        if (CategoryOf(id) is InventoryCategory.Tools or InventoryCategory.Gear)
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
    public void EquipStackable(int hotbarIndex, ushort id)
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
        // Worn gear is not held in hand: only tools and items go on the hotbar
        var alive = _tools.Any(t => t.InstanceId == instanceId && t.Item is not GearItem);
        _hotbar[i] = alive ? new HotbarSlot(true, 0, instanceId) : HotbarSlot.Empty;
        return alive;
    }

    public void ClearSlot(int hotbarIndex) => _hotbar[Wrap(hotbarIndex)] = HotbarSlot.Empty;

    /// <summary>Id shown in a hotbar slot, or 0 when it is empty or its tool is gone.</summary>
    public ushort IdAt(int index)
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
    public bool ContainsInHotbar(ushort id)
    {
        if (id == 0) return false;
        for (var i = 0; i < SlotCount; i++)
            if (IdAt(i) == id)
                return true;
        return false;
    }

    /// <summary>
    /// Selects the hotbar slot already showing <paramref name="id"/>, or equips it into the selected slot. A creative
    /// player needs to own none of it: a block it holds none of is granted one first, so the slot can place it (creative
    /// placing spends nothing).
    /// </summary>
    public bool PickBlock(ushort id, bool creative = false)
    {
        if (id == 0) return false;
        for (var i = 0; i < SlotCount; i++)
        {
            if (IdAt(i) != id) continue;
            Select(i);
            return true;
        }
        if (CountFor(id) <= 0 && !(creative && Add(id, 1))) return false;
        if (CategoryOf(id) is InventoryCategory.Tools or InventoryCategory.Gear)
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
    public int CountFor(ushort id)
    {
        var category = CategoryOf(id);
        return category switch
        {
            InventoryCategory.Blocks => _blocks.GetValueOrDefault(id),
            InventoryCategory.Misc => _misc.GetValueOrDefault(id),
            InventoryCategory.Tools or InventoryCategory.Gear => _tools.Count(t => t.DefinitionId == id),
            _ => 0,
        };
    }

    /// <summary>Recipes the pools can currently afford, including room for the result.</summary>
    public IReadOnlyList<CraftingRecipe> Craftable() => InventoryCrafting.Craftable(this);

    /// <summary>The recipes the Craft tab lists: all of them in creative, only the affordable ones in survival.</summary>
    public IReadOnlyList<CraftingRecipe> ListedRecipes(bool creative) =>
        creative ? VoxelEngine.Content.ContentRegistry.CraftingRecipes : Craftable();

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
        Func<int, CraftingRecipe.Ingredient, ushort>? pickMember) => InventoryCrafting.CanCraftBatch(this, recipe, batch, pickMember);

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
        Func<int, CraftingRecipe.Ingredient, ushort>? pickMember) => InventoryCrafting.TryCraft(this, recipe, batch, pickMember);

    /// <summary>Display name for a list row or preview heading.</summary>
    public static string DisplayName(ushort id)
    {
        if (BlockType.ById[id] != null) return BlockType.ById[id]!.DisplayName;
        if (ItemRegistry.TryGet(id) is { } item) return item.Name;
        return $"unknown {id}";
    }

    /// <summary>Stackable rows for a tab, holding something only (name-sorted).</summary>
    public IReadOnlyList<StackRow> StackEntries(InventoryCategory category)
    {
        if (category is InventoryCategory.Crafting or InventoryCategory.Tools or InventoryCategory.Gear)
            return [];
        var store = category == InventoryCategory.Blocks ? _blocks : _misc;
        // The panel's order: by display name, then the fuller stack first. Rows are always built
        // here, so the list is sorted by construction and a sort button would be a no-op.
        return store
            .Where(kv => kv.Value > 0)
            .OrderBy(kv => DisplayName(kv.Key), StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(kv => kv.Value)
            .Select(kv => new StackRow(kv.Key, DisplayName(kv.Key), kv.Value))
            .ToArray();
    }

    /// <summary>One row per tool instance (name-sorted, oldest first within a name).</summary>
    public IReadOnlyList<ToolRow> ToolEntries() => InstanceEntries(gear: false);

    /// <summary>One row per gear instance (name-sorted, oldest first within a name).</summary>
    public IReadOnlyList<ToolRow> GearEntries() => InstanceEntries(gear: true);

    private ToolRow[] InstanceEntries(bool gear) =>
        _tools
            .Where(t => (t.Item is GearItem) == gear)
            .OrderBy(t => DisplayName(t.DefinitionId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.InstanceId)
            .Select(t => new ToolRow(t.InstanceId, t.DefinitionId, DisplayName(t.DefinitionId)))
            .ToArray();

    /// <summary>Item id a tool instance currently is, or 0 when it is gone.</summary>
    public ushort ToolItemId(int instanceId) =>
        _tools.FirstOrDefault(t => t.InstanceId == instanceId)?.DefinitionId ?? 0;

    /// <summary>Live tool stack for an instance id, or null when it is gone. Used by the item
    /// use dispatcher to hand the override its per-instance state.</summary>
    public ItemStack? GetToolStack(int instanceId) => TryGetToolStack(instanceId, out var stack) ? stack : null;

    /// <summary>The live tool stack for an instance id, false when it is gone. Allocation-free (a plain
    /// loop), since the HUD reads the held staff's stack every frame.</summary>
    public bool TryGetToolStack(int instanceId, [NotNullWhen(true)] out ItemStack? stack)
    {
        stack = LiveTool(instanceId);
        return stack != null;
    }

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
    /// Snapshots the whole inventory for <c>player.json</c>: every pool entry, every tool
    /// instance with its components, the hotbar layout and the selection. Content is named
    /// through <see cref="SaveNames"/>; an id with no stable name is left out with one warning.
    /// </summary>
    public PlayerSaveData.InventoryData Export() => InventorySaveCodec.Export(this);

    /// <summary>
    /// Whether this player has ever obtained the id — the look-at label's "Unknown" rule (see
    /// <see cref="LookLabel"/>). Every credited id is marked in <see cref="Add(ushort, int, bool)"/>, a block the
    /// player mined in <see cref="MarkKnown"/>, and a restored save's obtained names are marked on import.
    /// </summary>
    public bool Knows(ushort id) => _known.Contains(id);

    /// <summary>
    /// Marks an id discovered without crediting it: the player mined that block (a break by TNT or any other
    /// non-player cause never calls this). False when it was already known.
    /// </summary>
    public bool MarkKnown(ushort id) => _known.Add(id);

    /// <summary>
    /// Moves whenever the spell parts this inventory knows really change (never for an equal set), so
    /// an open spell builder rebuilds its columns (<see cref="SpellBuilder.SpellBuilderView.Refresh"/>)
    /// only then.
    /// </summary>
    public int KnownPartsVersion { get; private set; }

    /// <summary>
    /// Whether this player has learned the spell part with key <paramref name="key"/>
    /// (<c>&lt;kind&gt;/&lt;name&gt;</c>). Read it through <see cref="Magic.KnownSpellParts.Knows"/>,
    /// the one known-part rule, which also lets creative know every part.
    /// </summary>
    public bool KnowsPart(string key) => _knownParts.Contains(key);

    /// <summary>How many spell parts this player has learned (what sizes the mana pool).</summary>
    public int KnownPartCount => _knownParts.Count;

    /// <summary>The spell part keys this player has learned.</summary>
    public IReadOnlyCollection<string> KnownParts => _knownParts;

    /// <summary>
    /// Learns every key in <paramref name="keys"/> and returns whether any was new;
    /// <see cref="KnownPartsVersion"/> moves once when one was. The keys are trusted: content load
    /// validates the ones an item teaches. Go through <see cref="Magic.KnownSpellParts.Learn"/> so the
    /// mana pool follows.
    /// </summary>
    public bool LearnParts(IEnumerable<string> keys)
    {
        var learned = false;
        foreach (var key in keys)
            learned |= _knownParts.Add(key);
        if (learned) KnownPartsVersion++;
        return learned;
    }

    /// <summary>
    /// Replaces the known spell parts with <paramref name="keys"/> (a save or a server snapshot). An
    /// equal set changes nothing, so a client importing every snapshot never rebuilds an open builder.
    /// </summary>
    internal void ReplaceKnownParts(IEnumerable<string> keys)
    {
        var next = new HashSet<string>(keys, StringComparer.Ordinal);
        if (next.SetEquals(_knownParts)) return;
        _knownParts.Clear();
        _knownParts.UnionWith(next);
        KnownPartsVersion++;
    }

    /// <summary>
    /// Replaces everything this inventory holds — the constructor's spawn stock included —
    /// with a saved one (main thread). Pool caps are honored, tool instance ids are kept (a
    /// repeated id is restored once) and later pickups get ids above every restored one; a
    /// durability or crucible value is clamped to this run's maximum. Names this run does not
    /// know, or that belong to the wrong pool, are dropped with one warning, and a hotbar slot
    /// pointing at dropped content comes back empty. The known spell parts are replaced by the saved
    /// keys this run still has (an unknown key is named in the same warning; a save from before known
    /// parts knows none). The discovered waystones are replaced by the saved ones (none for a save from before them).
    /// </summary>
    public void Import(PlayerSaveData.InventoryData data) => InventorySaveCodec.Import(this, data);

    internal Dictionary<ushort, int>? StoreOf(ushort id) => CategoryOf(id) switch
    {
        InventoryCategory.Blocks => _blocks,
        InventoryCategory.Misc => _misc,
        _ => null,
    };

    internal void ClearSlotsShowing(ushort id)
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

    private void EquipNewStackable(ushort id)
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
    /// with, and the pools it fills are the ones the category-cap tests measure against. It knows no
    /// spell part.
    /// </summary>
    public Inventory() : this(seedTestStock: true)
    {
    }

    /// <summary>A stockless pool that knows no spell part; a new survival player starts from it plus
    /// the starter kit.</summary>
    public static Inventory Empty() => new(seedTestStock: false);

    private Inventory(bool seedTestStock)
    {
        Equipment = new Equipment(this);
        if (!seedTestStock) return;
        foreach (var type in BlockType.Types.Values)
            if (type.HasItem && !type.Fluid)
                _blocks[type.Id] = StartingStock;
        // One bucket, since water needs one to reach, plus the axe, found by behavior and
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
    public readonly record struct StackRow(ushort Id, string Name, int Count);

    /// <summary>One tool list row: icon, name and the instance it equips.</summary>
    public readonly record struct ToolRow(int InstanceId, ushort ItemId, string Name);
}
