using System.Linq;
using System.Text;
using VoxelEngine.Content;
using VoxelEngine.Items;
using VoxelEngine.Networking;
using VoxelEngine.Saves;
using VoxelEngine.Simulation.Chests;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Chests;

/// <summary>Accepted saved misc instances remain complete chest instances during moves and station saves.</summary>
public sealed class ChestMiscInstanceTransferTests
{
    private const int SavedInstanceId = 481;
    private const string SavedDisplayName = "Saved chest copy";

    public ChestMiscInstanceTransferTests() => TestContent.EnsureLoaded();

    [Fact]
    public void ADepositedSavedMiscInstanceCanBeWithdrawnWithRoom()
    {
        var (inventory, itemId) = ImportSavedMiscInstance();
        var source = inventory.GetToolStack(SavedInstanceId)!;
        Assert.Equal(SavedDisplayName, source.ResolvedName);
        var chest = new ChestState(2);

        Assert.Equal(1, ChestAccess.Apply(inventory, chest, ChestMove.DepositInstance(SavedInstanceId)));
        Assert.Null(inventory.GetToolStack(SavedInstanceId));
        Assert.Equal(0, inventory.SelectedToolInstance);
        Assert.Equal(0, inventory.OffhandToolInstance);
        Assert.True(inventory.Hotbar[0].IsEmpty);
        Assert.Equal(itemId, chest[0].Id);
        Assert.Equal(SavedDisplayName, chest[0].Instance!.ResolvedName);

        Assert.Equal(1, ChestAccess.Apply(inventory, chest, ChestMove.Withdraw(0, 0)));
        Assert.True(chest.IsEmpty);
        var restoredEntry = Assert.Single(inventory.ToolEntries(), entry => entry.ItemId == itemId);
        Assert.True(restoredEntry.InstanceId > 0);
        Assert.NotEqual(SavedInstanceId, restoredEntry.InstanceId);
        Assert.Equal(SavedDisplayName, inventory.GetToolStack(restoredEntry.InstanceId)!.ResolvedName);
    }

    [Fact]
    public void ADepositedSavedMiscInstanceSurvivesStationSaveReload()
    {
        var (inventory, itemId) = ImportSavedMiscInstance();
        var chest = new ChestState(2);
        Assert.Equal(1, ChestAccess.Apply(inventory, chest, ChestMove.DepositInstance(SavedInstanceId)));
        Assert.Equal(itemId, chest[0].Id);
        Assert.Equal(SavedDisplayName, chest[0].Instance!.ResolvedName);

        var data = new StationSaveData { Chests = [ChestSaveCodec.Export(4, 5, 6, chest)] };
        var bytes = StationSaveCodec.ToBytes(data);
        var parsed = StationSaveCodec.TryParse(bytes);
        Assert.NotNull(parsed?.Chests);
        var dropped = new System.Collections.Generic.List<string>();
        var restored = ChestSaveCodec.Import(parsed!.Chests![0], dropped)!;
        using var writer = new PacketWriter(NetMessage.ChestContents);
        ChestNetCodec.WriteOpenContents(writer, 4, 5, 6, chest);
        using var reader = new PacketReader(writer.ToArray());
        var networkContents = ChestNetCodec.ReadContents(reader);
        Assert.NotNull(networkContents);
        Assert.Equal(SavedDisplayName, networkContents!.Value.Contents[0].Instance!.ResolvedName);

        Assert.Equal(itemId, restored[0].Id);
        Assert.NotNull(restored[0].Instance);
        Assert.Equal(SavedDisplayName, restored[0].Instance!.ResolvedName);
        Assert.Empty(dropped);
    }

    [Fact]
    public void AFullToolsPoolKeepsAMiscInstanceInTheChestUntilThereIsRoom()
    {
        var (inventory, itemId) = ImportSavedMiscInstance();
        var chest = new ChestState(2);
        Assert.Equal(1, ChestAccess.Apply(inventory, chest, ChestMove.DepositInstance(SavedInstanceId)));
        var toolId = ItemRegistry.All.First(candidate => Inventory.CategoryOf(candidate.Id) == InventoryCategory.Tools).Id;
        while (inventory.Add(toolId)) { }
        Assert.False(inventory.Add(toolId));

        Assert.Equal(0, ChestAccess.Apply(inventory, chest, ChestMove.Withdraw(0, 0)));
        Assert.Equal(SavedDisplayName, chest[0].Instance!.ResolvedName);
        var existingTool = inventory.ToolEntries().First(entry => entry.ItemId == toolId);
        Assert.True(inventory.RemoveTool(existingTool.InstanceId));
        Assert.Equal(1, ChestAccess.Apply(inventory, chest, ChestMove.Withdraw(0, 0)));
        Assert.True(chest.IsEmpty);
        var restored = Assert.Single(inventory.ToolEntries(), entry => entry.ItemId == itemId);
        Assert.Equal(SavedDisplayName, inventory.GetToolStack(restored.InstanceId)!.ResolvedName);
    }

    [Fact]
    public void AddInstanceStillRejectsAPlaceableBlock()
    {
        var inventory = Inventory.Empty();
        var stone = ContentRegistry.RequireBlock("stone").Id;

        Assert.False(inventory.AddInstance(new ItemStack(0, stone)));
        Assert.Equal(0, inventory.UsedOf(InventoryCategory.Blocks));
        Assert.Equal(0, inventory.UsedOf(InventoryCategory.Tools));
    }

    [Fact]
    public void AStackOfTheSameMiscDefinitionStillUsesTheStackTransferPath()
    {
        var (inventory, itemId) = ImportSavedMiscInstance();
        Assert.False(ChestTransfer.IsInstance(itemId));
        Assert.True(inventory.Add(itemId, 3));
        var chest = new ChestState(2);

        Assert.Equal(2, ChestAccess.Apply(inventory, chest, ChestMove.DepositStack(itemId, 2)));
        Assert.Equal(1, inventory.CountFor(itemId));
        Assert.Equal(2, chest.CountOf(itemId));
        Assert.Null(chest[0].Instance);
    }

    private static (Inventory Inventory, ushort ItemId) ImportSavedMiscInstance()
    {
        var item = ItemRegistry.All.First(candidate => Inventory.CategoryOf(candidate.Id) == InventoryCategory.Misc);
        var saveName = SaveNames.NameOf(item.Id)!;
        var json = "{\"Inventory\":{\"Tools\":[{\"InstanceId\":" + SavedInstanceId
            + ",\"Name\":\"" + saveName + "\",\"CustomName\":\"" + SavedDisplayName
            + "\"}],\"Hotbar\":[{\"IsTool\":true,\"ToolInstance\":" + SavedInstanceId
            + "}],\"Offhand\":{\"IsTool\":true,\"ToolInstance\":" + SavedInstanceId
            + "},\"NextToolInstance\":" + (SavedInstanceId + 1) + "}}";
        var parsed = PlayerSaveCodec.TryParse(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(parsed?.Inventory);

        var inventory = Inventory.Empty();
        inventory.Import(parsed!.Inventory!);
        Assert.Equal(item.Id, inventory.GetToolStack(SavedInstanceId)!.DefinitionId);
        Assert.Equal(SavedInstanceId, inventory.SelectedToolInstance);
        Assert.Equal(SavedInstanceId, inventory.OffhandToolInstance);
        return (inventory, item.Id);
    }
}
