using System.Linq;
using System.Numerics;
using System.Text;
using VoxelEngine.Interaction;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Saves;
using VoxelEngine.Simulation.WorkStations;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

[Collection("Worldgen seed")]
public sealed class StationInstanceInputTests
{
    public StationInstanceInputTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    [Fact]
    public void AcceptedSavedMiscInstanceIsSpentWhileRefusedAndCreativeInputsRemainOwned()
    {
        SupportTestWorld? world = null;
        var acceptedInstance = false;
        var acceptedMetalCount = 0;
        var acceptedAliasesCleared = false;
        var unrelatedInstanceRetained = false;
        var normalStackClickCount = 0;
        var remainingStackCount = -1;
        var refusedClickAccepted = true;
        var refusedInstanceRetained = false;
        var creativeClickAccepted = false;
        var creativeInstanceRetained = false;
        var cleanupCompleted = false;
        var bloom = ContentRegistry.RequireItem("bloom").Id;
        var anvilStackCap = WorkStationManager.StackCap("forge", bloom);

        try
        {
            world = new SupportTestWorld();
            var player = world.Player;
            var inventory = player.Inventory;
            var firstAnvil = new Vector3(8, world.Top + 1, 6);
            var creativeAnvil = new Vector3(10, world.Top + 1, 6);
            world.Load((int)firstAnvil.X, (int)firstAnvil.Y, (int)firstAnvil.Z);
            world.Load((int)creativeAnvil.X, (int)creativeAnvil.Y, (int)creativeAnvil.Z);
            var anvilBlock = BlockType.Types.Values.First(block => block.WorkStation?.Recipes == "forge");
            world.Set((int)firstAnvil.X, (int)firstAnvil.Y, (int)firstAnvil.Z, anvilBlock.Id);
            world.Set((int)creativeAnvil.X, (int)creativeAnvil.Y, (int)creativeAnvil.Z, anvilBlock.Id);

            inventory.Import(ParseSavedInstances(701, 702));
            Assert.Equal(bloom, inventory.SelectedBlockId);
            Assert.Equal(1, inventory.SelectedCount);
            Assert.Equal(701, inventory.SelectedToolInstance);
            Assert.True(ClickAnvil(world, player, firstAnvil));

            var acceptedState = world.World.Smithing.WorkStations.StateAt(
                (int)firstAnvil.X, (int)firstAnvil.Y, (int)firstAnvil.Z);
            Assert.NotNull(acceptedState);
            acceptedMetalCount = acceptedState.InputCount;
            acceptedInstance = inventory.GetToolStack(701) != null;
            acceptedAliasesCleared = inventory.SlotAt(0) == HotbarSlot.Empty
                && inventory.SlotAt(1) == HotbarSlot.Empty
                && inventory.Offhand == HotbarSlot.Empty;
            unrelatedInstanceRetained = inventory.GetToolStack(702) != null
                && inventory.SlotAt(2) == new HotbarSlot(true, 0, 702);

            var stackInputs = anvilStackCap - acceptedMetalCount;
            Assert.True(stackInputs > 0);
            Assert.True(inventory.Add(bloom, stackInputs));
            Assert.True(inventory.PickBlock(bloom));
            for (var i = 0; i < stackInputs; i++)
                if (ClickAnvil(world, player, firstAnvil))
                    normalStackClickCount++;
            remainingStackCount = inventory.CountFor(bloom);

            inventory.Import(ParseSavedInstances(703, 704));
            refusedClickAccepted = ClickAnvil(world, player, firstAnvil);
            refusedInstanceRetained = inventory.GetToolStack(703) != null
                && inventory.SelectedToolInstance == 703;
            var fullState = world.World.Smithing.WorkStations.StateAt(
                (int)firstAnvil.X, (int)firstAnvil.Y, (int)firstAnvil.Z);
            Assert.NotNull(fullState);
            Assert.Equal(anvilStackCap, fullState.InputCount);

            player.Creative = true;
            inventory.Import(ParseSavedInstances(705, 706));
            creativeClickAccepted = ClickAnvil(world, player, creativeAnvil);
            creativeInstanceRetained = inventory.GetToolStack(705) != null
                && inventory.SelectedToolInstance == 705;
        }
        finally
        {
            world?.Dispose();
            cleanupCompleted = true;
        }

        Assert.True(cleanupCompleted);
        Assert.Equal(1, acceptedMetalCount);
        Assert.Equal(anvilStackCap, acceptedMetalCount + normalStackClickCount);
        Assert.False(acceptedInstance);
        Assert.True(acceptedAliasesCleared);
        Assert.True(unrelatedInstanceRetained);
        Assert.Equal(0, remainingStackCount);
        Assert.False(refusedClickAccepted);
        Assert.True(refusedInstanceRetained);
        Assert.True(creativeClickAccepted);
        Assert.True(creativeInstanceRetained);
    }

    private static PlayerSaveData.InventoryData ParseSavedInstances(int inputInstance, int unrelatedInstance)
    {
        var json = $$"""
            {
              "Inventory": {
                "Tools": [
                  { "InstanceId": {{inputInstance}}, "Name": "item/bloom" },
                  { "InstanceId": {{unrelatedInstance}}, "Name": "item/coal" }
                ],
                "Hotbar": [
                  { "IsTool": true, "ToolInstance": {{inputInstance}} },
                  { "IsTool": true, "ToolInstance": {{inputInstance}} },
                  { "IsTool": true, "ToolInstance": {{unrelatedInstance}} }
                ],
                "Offhand": { "IsTool": true, "ToolInstance": {{inputInstance}} },
                "SelectedIndex": 0,
                "NextToolInstance": {{unrelatedInstance + 1}}
              }
            }
            """;
        var saved = PlayerSaveCodec.TryParse(Encoding.UTF8.GetBytes(json), out var rejectedJson);
        Assert.False(rejectedJson);
        Assert.NotNull(saved?.Inventory);
        return saved!.Inventory!;
    }

    private static bool ClickAnvil(SupportTestWorld world, ServerPlayer player, Vector3 cell)
    {
        var eye = player.Context.Eye;
        var target = cell + new Vector3(0.5f, 0.9f, 0.5f);
        player.Aim(eye, Vector3.Normalize(target - eye));
        return world.World.Interactions.TryWorkStationInteract(World.InteractionReach, player.Context);
    }
}
