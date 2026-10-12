using System;
using System.Linq;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class CreativeCatalogTests
{
    public CreativeCatalogTests() => TestContent.EnsureLoaded();

    [Theory]
    [InlineData(InventoryCategory.Blocks)]
    [InlineData(InventoryCategory.Tools)]
    [InlineData(InventoryCategory.Misc)]
    public void EntriesHoldExactlyEveryItemInTheirPool(InventoryCategory category)
    {
        var expected = ItemRegistry.All
            .Where(i => Inventory.CategoryOf(i.Id) == category)
            .Select(i => i.Id)
            .ToHashSet();

        var actual = CreativeCatalog.Entries(category, new Inventory())
            .Select(e => e.Id)
            .ToHashSet();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EntriesExcludeFluids()
    {
        var water = ContentRegistry.RequireBlock("water").Id;

        var blocks = CreativeCatalog.Entries(InventoryCategory.Blocks, new Inventory());

        Assert.DoesNotContain(blocks, e => e.Id == water);
    }

    [Fact]
    public void EntriesAreNameSorted()
    {
        var entries = CreativeCatalog.Entries(InventoryCategory.Blocks, new Inventory());

        for (var i = 1; i < entries.Count; i++)
            Assert.True(string.Compare(entries[i - 1].Name, entries[i].Name,
                StringComparison.OrdinalIgnoreCase) <= 0, "catalog entries are not name-sorted");
    }

    [Fact]
    public void EntriesReportTheOwnedCount()
    {
        var stock = CreativeCatalog.Entries(InventoryCategory.Blocks, new Inventory());
        var empty = CreativeCatalog.Entries(InventoryCategory.Blocks, Inventory.Empty());
        var stone = ContentRegistry.RequireBlock("stone").Id;

        Assert.True(Assert.Single(stock, e => e.Id == stone).Count > 0);
        Assert.Equal(0, Assert.Single(empty, e => e.Id == stone).Count);
    }

    [Fact]
    public void EmptyInventoryOwnsNothingButStillAccepts()
    {
        var inventory = Inventory.Empty();
        var stone = ContentRegistry.RequireBlock("stone").Id;

        Assert.Equal(0, inventory.UsedOf(InventoryCategory.Blocks));
        Assert.Equal(0, inventory.UsedOf(InventoryCategory.Tools));
        Assert.Equal(0, inventory.UsedOf(InventoryCategory.Misc));
        Assert.True(inventory.Add(stone, 3));
        Assert.Equal(3, inventory.CountFor(stone));
    }

    [Fact]
    public void RemoveAllClearsAStackableAndItsHotbarSlots()
    {
        var inventory = Inventory.Empty();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        Assert.True(inventory.Add(stone, 5));
        if (inventory.IdAt(0) != stone)
            inventory.EquipStackable(0, stone);

        Assert.True(inventory.RemoveAll(stone));
        Assert.Equal(0, inventory.CountFor(stone));
        Assert.Equal(0, inventory.IdAt(0));
        Assert.False(inventory.RemoveAll(stone));
    }

    [Fact]
    public void RemoveAllClearsEveryToolInstance()
    {
        var inventory = Inventory.Empty();
        var bucket = ContentRegistry.RequireItem("bucket").Id;
        Assert.True(inventory.Add(bucket, 3));
        Assert.Equal(3, inventory.CountFor(bucket));

        Assert.True(inventory.RemoveAll(bucket));
        Assert.Equal(0, inventory.CountFor(bucket));
        Assert.Empty(inventory.ToolEntries());
    }

    [Fact]
    public void CatalogButtonHitTestFindsEachButton()
    {
        var screen = new System.Numerics.Vector2(1920f, 1080f);
        const int rowCount = 4;

        for (var which = 0; which < InventoryPanelLayout.CatalogButtonCount; which++)
        {
            var centre = InventoryPanelLayout.CatalogButtonCentre(0, 0f, screen, rowCount, which);
            Assert.Equal((InventoryPanelLayout.CatalogButton)which,
                InventoryPanelLayout.CatalogButtonAt(centre, 0, 0f, rowCount, screen));
        }
    }

    [Fact]
    public void CatalogSearchReachesItemsThePlayerDoesNotOwn()
    {
        var empty = Inventory.Empty();
        var stone = ContentRegistry.RequireBlock("stone");

        Assert.DoesNotContain(InventorySearch.Search(stone.DisplayName, empty),
            h => h.Kind == SearchHitKind.Inventory);
        Assert.Contains(InventorySearch.Search(stone.DisplayName, empty, includeCatalog: true),
            h => h.Kind == SearchHitKind.Inventory && h.Id == stone.Id);
    }
}
