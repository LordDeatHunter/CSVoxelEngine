using System.Linq;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.UI.InventoryPanel;

/// <summary>A tab is where the panel lists an item, not how it is stored: a lantern keeps its own fuel as an instance yet is listed with the blocks.</summary>
public sealed class InventoryTabsTests
{
    public InventoryTabsTests() => TestContent.EnsureLoaded();

    private static ushort Lantern => ContentRegistry.RequireBlock("lantern").Id;
    private static ushort Stone => ContentRegistry.RequireBlock("stone").Id;

    [Fact]
    public void ALanternIsListedWithTheBlocksWhileStayingAnInstance()
    {
        Assert.Equal(InventoryCategory.Blocks, InventoryTabs.TabOf(Lantern));
        Assert.Equal(InventoryCategory.Tools, Inventory.CategoryOf(Lantern));

        var inventory = Inventory.Empty();
        Assert.True(inventory.Add(Stone, 3));
        Assert.True(inventory.Add(Lantern));

        var blocks = InventoryTabs.Entries(inventory, InventoryCategory.Blocks);
        var row = Assert.Single(blocks, r => r.Id == Lantern);
        Assert.NotEqual(0, row.InstanceId);
        Assert.Equal(3, Assert.Single(blocks, r => r.Id == Stone).Count);
        Assert.DoesNotContain(InventoryTabs.Entries(inventory, InventoryCategory.Tools), r => r.Id == Lantern);
    }

    [Fact]
    public void EveryHeldInstanceIsListedUnderExactlyItsOwnTab()
    {
        var inventory = Inventory.Empty();
        Assert.True(inventory.Add(Lantern));
        Assert.True(inventory.Add(Lantern));
        Assert.True(inventory.Add(Stone, 2));

        var listed = new[] { InventoryCategory.Blocks, InventoryCategory.Tools, InventoryCategory.Misc, InventoryCategory.Gear }
            .SelectMany(tab => InventoryTabs.Entries(inventory, tab).Where(r => r.InstanceId != 0))
            .Select(r => r.InstanceId)
            .ToArray();

        Assert.Equal(inventory.ToolEntries().Select(t => t.InstanceId).Order(), listed.Order());
    }
}
