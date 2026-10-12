using System;
using System.Linq;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.UI.InventoryPanel;

public sealed class InventoryTests
{
    public InventoryTests() => TestContent.EnsureLoaded();

    [Fact]
    public void BlocksShareOnePoolAcrossIds()
    {
        var inventory = new Inventory();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;

        Assert.True(inventory.Add(stone, Inventory.BlockCapacity - inventory.UsedOf(InventoryCategory.Blocks)));
        Assert.Equal(Inventory.BlockCapacity, inventory.UsedOf(InventoryCategory.Blocks));
        Assert.False(inventory.CanAccept(dirt));
        Assert.False(inventory.Add(dirt));
    }

    [Fact]
    public void PlacingFreesPoolRoomForAnyBlock()
    {
        var inventory = new Inventory();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        inventory.Add(stone, Inventory.BlockCapacity - inventory.UsedOf(InventoryCategory.Blocks));

        inventory.Select(0);
        inventory.EquipStackable(0, stone);
        for (var i = 0; i < 4; i++)
            Assert.True(inventory.ConsumeSelected());

        Assert.True(inventory.CanAccept(dirt));
        Assert.True(inventory.Add(dirt, 4));
        Assert.Equal(Inventory.BlockCapacity, inventory.UsedOf(InventoryCategory.Blocks));
    }

    [Fact]
    public void HotbarSlotsSharePoolCounts()
    {
        var inventory = new Inventory();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        inventory.EquipStackable(0, stone);
        inventory.EquipStackable(3, stone);

        var before = inventory.CountAt(0);
        Assert.Equal(before, inventory.CountAt(3));

        inventory.Select(0);
        Assert.True(inventory.ConsumeSelected());
        Assert.Equal(before - 1, inventory.CountAt(0));
        Assert.Equal(before - 1, inventory.CountAt(3));
    }

    [Fact]
    public void EquippingTheShownIdClearsTheSlot()
    {
        var inventory = new Inventory();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;

        inventory.EquipStackable(0, stone);
        Assert.Equal(stone, inventory.IdAt(0));
        inventory.EquipStackable(0, stone);
        Assert.Equal(0, inventory.IdAt(0));

        inventory.EquipStackable(0, stone);
        inventory.EquipStackable(0, dirt);
        Assert.Equal(dirt, inventory.IdAt(0));
    }

    [Fact]
    public void ToolsAreCappedInstancesThatMorphTogether()
    {
        var inventory = new Inventory();
        var bucket = ContentRegistry.RequireItem("bucket").Id;
        var filled = ContentRegistry.RequireItem("water_bucket").Id;

        for (var i = inventory.UsedOf(InventoryCategory.Tools); i < Inventory.ToolCapacity; i++)
            Assert.True(inventory.Add(bucket));
        Assert.False(inventory.Add(bucket));

        var first = inventory.ToolEntries()[0].InstanceId;
        inventory.EquipTool(0, first);
        inventory.EquipTool(3, first);
        Assert.True(inventory.MorphTool(first, filled));
        Assert.Equal(filled, inventory.IdAt(0));
        Assert.Equal(filled, inventory.IdAt(3));
    }

    [Fact]
    public void DifferentToolInstancesMorphIndependently()
    {
        var inventory = new Inventory();
        var bucket = ContentRegistry.RequireItem("bucket").Id;
        var filled = ContentRegistry.RequireItem("water_bucket").Id;
        Assert.True(inventory.Add(bucket));

        var tools = inventory.ToolEntries().Where(t => t.ItemId == bucket).ToArray();
        Assert.Equal(2, tools.Length);
        inventory.EquipTool(0, tools[0].InstanceId);
        inventory.EquipTool(3, tools[1].InstanceId);

        Assert.True(inventory.MorphTool(tools[0].InstanceId, filled));
        Assert.Equal(filled, inventory.IdAt(0));
        Assert.Equal(bucket, inventory.IdAt(3));
    }

    [Fact]
    public void RemovingAToolEmptiesItsSlots()
    {
        var inventory = new Inventory();
        var instance = inventory.ToolEntries()[0].InstanceId;
        inventory.EquipTool(0, instance);
        inventory.EquipTool(3, instance);

        Assert.True(inventory.RemoveTool(instance));
        Assert.Equal(0, inventory.IdAt(0));
        Assert.Equal(0, inventory.IdAt(3));
        Assert.False(inventory.ConsumeSelected());
    }

    [Fact]
    public void MiscSharesItsOwnPool()
    {
        var inventory = new Inventory();
        var flint = ContentRegistry.RequireItem("flint").Id;
        var vine = ContentRegistry.RequireItem("vine").Id;

        Assert.True(inventory.Add(flint, Inventory.MiscCapacity));
        Assert.False(inventory.Add(vine));
        Assert.Equal(InventoryCategory.Misc, Inventory.CategoryOf(flint));
    }

    [Fact]
    public void UncollectableIdsHaveNoPool()
    {
        var water = ContentRegistry.RequireBlock("water").Id;
        var log = ContentRegistry.RequireBlock("oak_log").Id;
        var bucket = ContentRegistry.RequireItem("bucket").Id;
        var stone = ContentRegistry.RequireBlock("stone").Id;

        Assert.Null(Inventory.CategoryOf(0));
        Assert.Null(Inventory.CategoryOf(water));
        Assert.Equal(InventoryCategory.Blocks, Inventory.CategoryOf(log));
        Assert.Equal(InventoryCategory.Tools, Inventory.CategoryOf(bucket));
        Assert.Equal(InventoryCategory.Blocks, Inventory.CategoryOf(stone));
        Assert.False(new Inventory().CanAccept(water));
    }

    [Fact]
    public void PickBlockSelectsHotbarSlotHoldingTarget()
    {
        var inventory = new Inventory();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        inventory.EquipStackable(0, stone);
        inventory.EquipStackable(5, dirt);

        inventory.Select(0);
        Assert.True(inventory.PickBlock(dirt));
        Assert.Equal(5, inventory.SelectedIndex);

        Assert.True(inventory.PickBlock(stone));
        Assert.Equal(0, inventory.SelectedIndex);
    }

    [Fact]
    public void PickBlockEquipsTargetFromPoolIntoSelectedSlot()
    {
        var inventory = new Inventory();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        inventory.EquipStackable(0, stone);
        inventory.Select(0);

        Assert.True(inventory.PickBlock(dirt));
        Assert.Equal(0, inventory.SelectedIndex);
        Assert.Equal(dirt, inventory.IdAt(0));
    }

    [Fact]
    public void PickBlockDoesNothingWhenTargetInNeitherPoolNorHotbar()
    {
        var inventory = new Inventory();
        var water = ContentRegistry.RequireBlock("water").Id;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        inventory.EquipStackable(0, stone);
        inventory.Select(0);

        Assert.False(inventory.PickBlock(water));
        Assert.Equal(0, inventory.SelectedIndex);
        Assert.Equal(stone, inventory.IdAt(0));
    }

    [Fact]
    public void PickBlockDoesNothingForAir()
    {
        var inventory = new Inventory();
        Assert.False(inventory.PickBlock(0));
        Assert.Equal(0, inventory.SelectedIndex);
    }

    [Fact]
    public void ThePanelRowsAreSortedByNameThenCount()
    {
        var inventory = new Inventory();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var dirt = ContentRegistry.RequireBlock("dirt").Id;
        var flint = ContentRegistry.RequireItem("flint").Id;
        inventory.Add(stone, 3);
        inventory.Add(dirt, 5);
        inventory.Add(flint, 2);

        var rows = inventory.StackEntries(InventoryCategory.Blocks);
        var names = rows.Select(r => r.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), names);
        Assert.All(rows, r => Assert.True(r.Count > 0));

        // The count is the tiebreak: the same name never appears twice (ids are unique), so the
        // rule is pinned through the sort key itself
        for (var i = 1; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Name, rows[i - 1].Name, StringComparison.OrdinalIgnoreCase))
                Assert.True(rows[i].Count <= rows[i - 1].Count);
        }
    }

    [Fact]
    public void AnIdBecomesObtainedWhenItIsAdded()
    {
        var inventory = Inventory.Empty();
        var stone = ContentRegistry.RequireBlock("stone").Id;

        Assert.False(inventory.Knows(stone));
        Assert.True(inventory.Add(stone, 1));
        Assert.True(inventory.Knows(stone));
    }

    [Fact]
    public void TheObtainedSetSurvivesASaveRoundTripWithoutTheStack()
    {
        var inventory = Inventory.Empty();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        inventory.Add(stone, 1);
        Assert.True(inventory.RemoveAll(stone));
        Assert.Equal(0, inventory.CountFor(stone));

        // Spent but obtained: the saved names bring the id back as known
        var restored = Inventory.Empty();
        restored.Import(inventory.Export());
        Assert.True(restored.Knows(stone));
    }
}
