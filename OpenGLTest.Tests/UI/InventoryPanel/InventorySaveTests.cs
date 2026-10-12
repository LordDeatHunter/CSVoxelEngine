using System.Collections.Generic;
using System.Linq;
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

    private static byte Stone => ContentRegistry.RequireBlock("stone").Id;
    private static byte Dirt => ContentRegistry.RequireBlock("dirt").Id;

    private static Item ToolItem(System.Func<Item, bool> predicate) =>
        ItemRegistry.All.First(i => Inventory.CategoryOf(i.Id) == InventoryCategory.Tools && predicate(i));

    private static Item MiscItem() =>
        ItemRegistry.All.First(i => Inventory.CategoryOf(i.Id) == InventoryCategory.Misc);

    private static int InstanceOf(Inventory inventory, byte itemId) =>
        inventory.ToolEntries().Last(t => t.ItemId == itemId).InstanceId;

    [Fact]
    public void ExportImportRoundTripsEveryPoolToolAndSlot()
    {
        var a = new Inventory();
        a.EquipStackable(0, Stone);
        a.Select(0);
        for (var i = 0; i < 3; i++)
            Assert.True(a.ConsumeSelected());

        var misc = MiscItem();
        Assert.True(a.Add(misc.Id, 4));

        var crucibleItem = ToolItem(i => i.CrucibleCapacityMl > 0);
        Assert.True(a.Add(crucibleItem.Id));
        var crucibleInstance = InstanceOf(a, crucibleItem.Id);
        var material = ContentRegistry.MoltenMaterials.First().Id;
        a.GetToolStack(crucibleInstance)!.Set(new CrucibleComponent(material, 40, 60, Stone));

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
        var b = new Inventory();
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
        var data = new Inventory().Export();
        var omitted = data.Blocks!.First(s => SaveNames.TryResolve(s.Name, out var id) && id == Stone);
        data.Blocks!.Remove(omitted);

        var b = new Inventory();
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

        var inventory = new Inventory();
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
        var full = new Inventory();
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
        var inventory = new Inventory();
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

        foreach (var stack in stacks)
            foreach (var type in stack.ComponentTypes)
                Assert.Contains(type, Inventory.HandledComponentTypes);
    }
}
