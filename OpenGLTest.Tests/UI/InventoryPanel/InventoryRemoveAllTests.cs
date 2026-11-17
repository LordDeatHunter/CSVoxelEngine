using System.Linq;
using System.Text;
using VoxelEngine.Networking;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Saves;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.UI.InventoryPanel;

public sealed class InventoryRemoveAllTests
{
    public InventoryRemoveAllTests() => TestContent.EnsureLoaded();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreativeClearRemovesRestoredMiscInstancesAndTheirAliases(bool hasStack)
    {
        var bandage = ContentRegistry.RequireItem("bandage");
        var bucket = ContentRegistry.RequireItem("bucket");
        var coal = ContentRegistry.RequireItem("coal");
        Assert.Equal(InventoryCategory.Misc, Inventory.CategoryOf(bandage.Id));
        var bandageName = SaveNames.NameOf(bandage.Id)!;
        var bucketName = SaveNames.NameOf(bucket.Id)!;
        var coalName = SaveNames.NameOf(coal.Id)!;
        var targetStack = hasStack ? $$"""{"name":"{{bandageName}}","count":4},""" : "";
        var stackAlias = $$"""{"isTool":false,"name":"{{bandageName}}"},""";

        var json = $$"""
        {
          "inventory": {
            "misc": [{{targetStack}} {"name":"{{coalName}}","count":3}],
            "tools": [
              {"instanceId":41,"name":"{{bandageName}}"},
              {"instanceId":42,"name":"{{bandageName}}"},
              {"instanceId":43,"name":"{{bucketName}}"}
            ],
            "hotbar": [
              {"isTool":true,"toolInstance":41},
              {"isTool":true,"toolInstance":41},
              {{stackAlias}}
              {"isTool":true,"toolInstance":42},
              {"isTool":true,"toolInstance":43},
              {"isTool":false,"name":"{{coalName}}"}
            ],
            "offhand": {"isTool":true,"toolInstance":41}
          }
        }
        """;
        var saved = PlayerSaveCodec.TryParse(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(saved?.Inventory);

        var inventory = Inventory.Empty();
        inventory.Import(saved!.Inventory!);

        Assert.Equal(2, inventory.ToolEntries().Count(entry => entry.ItemId == bandage.Id));
        Assert.Equal(1, inventory.ToolEntries().Count(entry => entry.ItemId == bucket.Id));
        Assert.Equal(hasStack ? 4 : 0, inventory.CountFor(bandage.Id));
        Assert.Equal(3, inventory.CountFor(coal.Id));
        Assert.Equal(41, inventory.ToolInstanceAt(0));
        Assert.Equal(41, inventory.ToolInstanceAt(1));
        Assert.Equal(hasStack ? bandage.Id : (ushort)0, inventory.IdAt(2));
        Assert.Equal(42, inventory.ToolInstanceAt(3));
        Assert.Equal(43, inventory.ToolInstanceAt(4));
        Assert.Equal(coal.Id, inventory.IdAt(5));
        Assert.Equal(41, inventory.OffhandToolInstance);

        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.CreativeClear(bandage.Id), creative: false));
        Assert.Equal(2, inventory.ToolEntries().Count(entry => entry.ItemId == bandage.Id));
        Assert.Equal(hasStack ? 4 : 0, inventory.CountFor(bandage.Id));

        Assert.True(InventoryIntentRules.Apply(inventory, InventoryIntent.CreativeClear(bandage.Id), creative: true));

        Assert.Equal(0, inventory.CountFor(bandage.Id));
        Assert.DoesNotContain(inventory.ToolEntries(), entry => entry.ItemId == bandage.Id);
        Assert.Equal(0, inventory.IdAt(0));
        Assert.Equal(0, inventory.IdAt(1));
        Assert.Equal(0, inventory.IdAt(2));
        Assert.Equal(0, inventory.IdAt(3));
        Assert.Equal(43, inventory.ToolInstanceAt(4));
        Assert.Equal(coal.Id, inventory.IdAt(5));
        Assert.Equal(0, inventory.OffhandToolInstance);
        Assert.Equal(3, inventory.CountFor(coal.Id));
        Assert.Contains(inventory.ToolEntries(), entry => entry.ItemId == bucket.Id);
        Assert.False(InventoryIntentRules.Apply(inventory, InventoryIntent.CreativeClear(bandage.Id), creative: true));
    }
}
