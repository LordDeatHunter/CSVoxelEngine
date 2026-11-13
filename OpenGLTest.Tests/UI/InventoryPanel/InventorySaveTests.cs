using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Spells;
using VoxelEngine.Items;
using VoxelEngine.Saves;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.UI.InventoryPanel;

/// <summary>
/// The inventory's save seam: <see cref="Inventory.Export"/> and <see cref="Inventory.Import"/>
/// must round-trip every pool, tool instance and hotbar slot by name, replace rather than merge
/// with the spawn stock, drop what this run cannot resolve and never break the pool caps or the
/// uniqueness of tool instance ids.
/// </summary>
public sealed class InventorySaveTests
{
    public InventorySaveTests() => TestContent.EnsureLoaded();

    private static ushort Stone => ContentRegistry.RequireBlock("stone").Id;
    private static ushort Dirt => ContentRegistry.RequireBlock("dirt").Id;

    private static Item ToolItem(System.Func<Item, bool> predicate) =>
        ItemRegistry.All.First(i => Inventory.CategoryOf(i.Id) == InventoryCategory.Tools && predicate(i));

    private static Item MiscItem() =>
        ItemRegistry.All.First(i => Inventory.CategoryOf(i.Id) == InventoryCategory.Misc);

    private static int InstanceOf(Inventory inventory, ushort itemId) =>
        inventory.ToolEntries().Last(t => t.ItemId == itemId).InstanceId;

    private static string[] SortedKnownParts(Inventory inventory) =>
        inventory.KnownParts.Order(System.StringComparer.Ordinal).ToArray();

    [Fact]
    public void KnownSpellPartsRoundTripByKey()
    {
        var a = Inventory.Empty();
        a.LearnParts(SpellPartRegistry.All.Take(3).Select(p => p.Key));
        var b = Inventory.Empty();
        b.LearnParts(SpellPartRegistry.All.Skip(3).Select(p => p.Key));

        b.Import(a.Export());

        Assert.NotEmpty(a.KnownParts);
        Assert.Equal(SortedKnownParts(a), SortedKnownParts(b));
    }

    [Fact]
    public void AnUnknownSpellPartKeyIsDroppedAndTheRestKept()
    {
        var a = Inventory.Empty();
        a.LearnParts(SpellPartRegistry.All.Take(2).Select(p => p.Key));
        var data = a.Export();
        data.KnownParts!.Add("element/no_such_part");
        data.KnownParts.Add(null!);

        var b = Inventory.Empty();
        b.Import(data);

        Assert.Equal(SortedKnownParts(a), SortedKnownParts(b));
        Assert.False(b.KnowsPart("element/no_such_part"));
    }

    [Fact]
    public void ReimportingTheSameKnownPartsDoesNotMoveTheVersion()
    {
        var a = Inventory.Empty();
        a.LearnParts(SpellPartRegistry.All.Take(2).Select(p => p.Key));
        var data = a.Export();
        var b = Inventory.Empty();
        b.Import(data);
        var version = b.KnownPartsVersion;

        b.Import(data);

        Assert.Equal(version, b.KnownPartsVersion);
    }

    [Fact]
    public void DiscoveredWaystonesRoundTripWithTheirNames()
    {
        var a = Inventory.Empty();
        a.Waystones.Add(1, 2, 3, "Kafuziro");
        a.Waystones.Add(-5, 300, 9, "Sky Gate");
        var b = Inventory.Empty();
        b.Waystones.Add(7, 7, 7, "Stale");

        b.Import(a.Export());

        Assert.Equal(a.Waystones.Entries, b.Waystones.Entries);
        Assert.False(b.Waystones.Contains(7, 7, 7));
    }

    [Fact]
    public void ASaveFromBeforeWaystonesKnowsNoneAndABrokenEntryIsDropped()
    {
        var a = Inventory.Empty();
        a.Waystones.Add(1, 2, 3, "Kafuziro");
        var data = a.Export();
        data.Waystones!.Add(new PlayerSaveData.DiscoveredWaystoneData { Cell = [4, 5], Name = "Bad" });
        data.Waystones.Add(new PlayerSaveData.DiscoveredWaystoneData { Cell = [4, 5, 6], Name = " \t " });
        data.Waystones.Add(null!);

        var b = Inventory.Empty();
        b.Import(data);
        Assert.Equal(a.Waystones.Entries, b.Waystones.Entries);

        data.Waystones = null;
        b.Import(data);
        Assert.Equal(0, b.Waystones.Count);
    }

    [Fact]
    public void ReimportingTheSameWaystonesDoesNotMoveTheVersion()
    {
        var a = Inventory.Empty();
        a.Waystones.Add(1, 2, 3, "Kafuziro");
        var data = a.Export();
        var b = Inventory.Empty();
        b.Import(data);
        var version = b.Waystones.Version;

        b.Import(data);
        Assert.Equal(version, b.Waystones.Version);

        data.Waystones![0].Name = "Renamed";
        b.Import(data);
        Assert.NotEqual(version, b.Waystones.Version);
    }

    [Fact]
    public void ExportImportRoundTripsEveryPoolToolAndSlot()
    {
        var a = TestInventory.Stocked();
        a.EquipStackable(0, Stone);
        a.Select(0);
        for (var i = 0; i < 3; i++)
            Assert.True(a.ConsumeSelected());

        var misc = MiscItem();
        Assert.True(a.Add(misc.Id, 4));

        var crucibleItem = ToolItem(i => i.CrucibleCapacityMl > 0);
        Assert.True(a.Add(crucibleItem.Id));
        var crucibleInstance = InstanceOf(a, crucibleItem.Id);
        // Two metals, so every metal the vessel holds survives the trip, not just one
        a.GetToolStack(crucibleInstance)!.Set(new CrucibleComponent(
            [
                new CrucibleMetal(ContentRegistry.MoltenMaterials[0].Id, 40, 60),
                new CrucibleMetal(ContentRegistry.MoltenMaterials[1].Id, 0, 20),
            ], Stone));

        var durableItem = ToolItem(i => i.MaxDurability > 2);
        Assert.True(a.Add(durableItem.Id));
        var durableInstance = InstanceOf(a, durableItem.Id);
        Assert.False(a.DamageTool(durableInstance, 2));
        a.GetToolStack(durableInstance)!.Rename("Old Faithful");
        a.GetToolStack(durableInstance)!.Enchantments["sharp"] = 2;

        a.EquipTool(5, crucibleInstance);
        a.EquipTool(7, durableInstance);
        a.EquipStackable(8, Dirt);
        a.Select(4);

        var data = a.Export();
        var b = TestInventory.Stocked();
        b.Import(data);

        foreach (var id in ItemRegistry.ById.Keys)
            Assert.Equal(a.CountFor(id), b.CountFor(id));

        var aTools = a.ToolEntries().Select(t => (t.InstanceId, t.ItemId)).ToArray();
        var bTools = b.ToolEntries().Select(t => (t.InstanceId, t.ItemId)).ToArray();
        Assert.Equal(aTools, bTools);
        foreach (var (instance, _) in aTools)
        {
            var sa = a.GetToolStack(instance)!;
            var sb = b.GetToolStack(instance)!;
            Assert.Equal(a.ToolDurabilityLeft(instance), b.ToolDurabilityLeft(instance));
            Assert.Equal(sa.ResolvedName, sb.ResolvedName);
            Assert.Equal(sa.Get<CrucibleComponent>(), sb.Get<CrucibleComponent>());
            Assert.Equal(sa.Enchantments.OrderBy(e => e.Key), sb.Enchantments.OrderBy(e => e.Key));
        }

        for (var i = 0; i < Inventory.SlotCount; i++)
            Assert.Equal(a.SlotAt(i), b.SlotAt(i));
        Assert.Equal(a.SelectedIndex, b.SelectedIndex);
    }

    [Fact]
    public void ImportReplacesRatherThanMerges()
    {
        var data = TestInventory.Stocked().Export();
        var omitted = data.Blocks!.First(s => SaveNames.TryResolve(s.Name, out var id) && id == Stone);
        data.Blocks!.Remove(omitted);

        var b = TestInventory.Stocked();
        Assert.True(b.CountFor(Stone) > 0);
        b.Import(data);

        Assert.Equal(0, b.CountFor(Stone));
        Assert.Equal(data.Blocks.Sum(s => s.Count ?? 0), b.UsedOf(InventoryCategory.Blocks));
    }

    [Fact]
    public void UnknownNamesAndDeadReferencesAreDroppedNotFatal()
    {
        var stoneName = SaveNames.NameOf(Stone)!;
        var data = new PlayerSaveData.InventoryData
        {
            Blocks =
            [
                new() { Name = "block/no-such", Count = 5 },
                new() { Name = "item/nope", Count = 5 },
                new() { Name = stoneName, Count = 7 },
            ],
            Tools = [new() { InstanceId = 3, Name = "item/no-such-tool" }],
            Hotbar =
            [
                new() { IsTool = true, ToolInstance = 3 },
                new() { IsTool = false, Name = "block/no-such" },
                new() { IsTool = false, Name = stoneName },
            ],
        };

        var inventory = TestInventory.Stocked();
        inventory.Import(data);

        Assert.Equal(7, inventory.CountFor(Stone));
        Assert.Empty(inventory.ToolEntries());
        Assert.Equal(HotbarSlot.Empty, inventory.SlotAt(0));
        Assert.Equal(HotbarSlot.Empty, inventory.SlotAt(1));
        Assert.Equal(new HotbarSlot(false, Stone, 0), inventory.SlotAt(2));
    }

    [Fact]
    public void CapsAreHonoredAndInstanceIdsStayUnique()
    {
        var toolItem = ToolItem(_ => true);
        var toolName = SaveNames.NameOf(toolItem.Id)!;

        var overfull = new PlayerSaveData.InventoryData
        {
            Blocks = [new() { Name = SaveNames.NameOf(Stone), Count = Inventory.BlockCapacity * 2 }],
            Tools = Enumerable.Range(1, Inventory.ToolCapacity + 3)
                .Select(i => new PlayerSaveData.ToolData { InstanceId = i, Name = toolName })
                .ToList(),
        };
        var full = TestInventory.Stocked();
        full.Import(overfull);
        Assert.True(full.UsedOf(InventoryCategory.Blocks) <= Inventory.CapacityOf(InventoryCategory.Blocks));
        Assert.True(full.UsedOf(InventoryCategory.Tools) <= Inventory.ToolCapacity);

        // A repeated instance id is restored once, and a stale NextToolInstance cannot make a
        // later pickup reuse a restored id.
        var shared = new PlayerSaveData.InventoryData
        {
            Tools =
            [
                new() { InstanceId = 7, Name = toolName },
                new() { InstanceId = 7, Name = toolName },
                new() { InstanceId = 12, Name = toolName },
            ],
            NextToolInstance = 1,
        };
        var inventory = TestInventory.Stocked();
        inventory.Import(shared);
        var restored = inventory.ToolEntries().Select(t => t.InstanceId).ToArray();
        Assert.Equal(restored.Distinct().Count(), restored.Length);
        Assert.Equal(2, restored.Length);

        Assert.True(inventory.Add(toolItem.Id));
        var fresh = inventory.ToolEntries().Select(t => t.InstanceId).Except(restored).Single();
        Assert.True(fresh > restored.Max());
    }

    [Fact]
    public void EveryStackComponentIsCoveredByTheCodec()
    {
        var stacks = new List<ItemStack>();
        foreach (var item in ItemRegistry.All)
            stacks.Add(new ItemStack(1, item));
        var crucible = new ItemStack(2, ToolItem(i => i.CrucibleCapacityMl > 0));
        crucible.Set(new CrucibleComponent(ContentRegistry.MoltenMaterials.First().Id, 10, 10, Stone));
        stacks.Add(crucible);
        var staff = ItemRegistry.All.OfType<StaffItem>().First();
        var bound = new ItemStack(3, staff);
        bound.Set(BoundStaff(staff, 0));
        stacks.Add(bound);

        foreach (var stack in stacks)
            foreach (var type in stack.ComponentTypes)
                Assert.Contains(type, InventorySaveCodec.HandledComponentTypes);
    }

    // A valid element + shape recipe the staff's own tags allow, picked by rule, never by name
    private static SpellPart[] ValidRecipe(StaffItem staff) =>
    [
        SpellPartRegistry.All.First(p => p.Kind == SpellPartKind.Element && p.IsAllowedBy(staff.AllowedTags)),
        SpellPartRegistry.All.First(p => p.Kind == SpellPartKind.Shape && p.IsAllowedBy(staff.AllowedTags)),
    ];

    private static StaffComponent BoundStaff(StaffItem staff, int slot)
    {
        Assert.True(StaffBinding.TryBind(staff, default, slot, ValidRecipe(staff), out var spells, out _));
        return spells;
    }

    [Fact]
    public void AStaffsBoundSpellsRoundTripByPartKey()
    {
        var staff = ItemRegistry.All.OfType<StaffItem>().First();
        var source = Inventory.Empty();
        Assert.True(source.Add(staff.Id));
        var stack = source.GetToolStack(InstanceOf(source, staff.Id))!;
        var spells = StaffBinding.Cycle(staff, BoundStaff(staff, staff.Slots - 1), 1);
        stack.Set(spells);

        var data = source.Export();
        var saved = Assert.Single(data.Tools!, t => t.InstanceId == stack.InstanceId).Staff;
        Assert.NotNull(saved);
        Assert.NotNull(saved.Slots);
        Assert.Equal(ValidRecipe(staff).Select(p => p.Key), saved.Slots[staff.Slots - 1]);

        var restored = Inventory.Empty();
        restored.Import(data);
        Assert.Equal(spells, restored.GetToolStack(stack.InstanceId)!.Get<StaffComponent>());
    }

    [Fact]
    public void AStaffRestoreDropsWhatThisRunCannotBind()
    {
        var staff = ItemRegistry.All.OfType<StaffItem>().First(s => s.Slots >= 3);
        var valid = ValidRecipe(staff).Select(p => p.Key).ToList();
        var slots = new List<List<string>?>(new List<string>?[staff.Slots + 1]);
        slots[0] = ["element/no_such_part", valid[1]];
        slots[1] = [valid[0]];
        slots[2] = valid;
        slots[staff.Slots] = valid;

        var restored = Inventory.Empty();
        restored.Import(new PlayerSaveData.InventoryData
        {
            Tools =
            [
                new PlayerSaveData.ToolData
                {
                    InstanceId = 1, Name = SaveNames.NameOf(staff.Id),
                    Staff = new PlayerSaveData.StaffData { Slots = slots, Selected = 99 },
                },
            ],
        });

        var spells = restored.GetToolStack(1)!.Get<StaffComponent>();
        Assert.Equal(1, spells.BoundCount);
        Assert.Equal(valid, spells.PartsAt(2));
        Assert.Empty(spells.PartsAt(staff.Slots));
        Assert.InRange(spells.Selected, 0, staff.Slots - 1);
    }
}
