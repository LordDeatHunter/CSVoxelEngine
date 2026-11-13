using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Crucibles;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// The crucible's right-clicks through the real chain: pouring into a mold under the crosshair, lifting a standing
/// crucible straight into the hand, and which surfaces may carry one.
/// </summary>
[Collection("Worldgen seed")]
public sealed class CrucibleInteractionTests : IDisposable
{
    private readonly SupportTestWorld _world;

    public CrucibleInteractionTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
        _world = new SupportTestWorld();
    }

    public void Dispose()
    {
        _world.Dispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static byte Copper => ContentRegistry.MoltenMaterialByName("copper")!.Id;

    private ItemStack HoldCrucible(int moltenMl)
    {
        var id = ContentRegistry.RequireItem("crucible").Id;
        var stack = CrucibleInventory.Add(_world.Player.Inventory, id, new CrucibleComponent(Copper, moltenMl, 0))!;
        Assert.True(_world.Player.Inventory.PickBlock(id));
        return stack;
    }

    private bool Click(Vector3 aimAt, out RightClickOutcome outcome)
    {
        var eye = _world.Player.Context.Eye;
        _world.Player.Aim(eye, Vector3.Normalize(aimAt - eye));
        return _world.World.RightClick.TryUseSelected(World.InteractionReach, _world.Player.Context, out outcome);
    }

    [Fact]
    public void AFullCrucibleClickedOnAnIngotMoldStartsPouringIntoIt()
    {
        var mold = ContentRegistry.RequireBlock("fired_mold_ingot").Id;
        _world.Load(8, _world.Top, 8);
        _world.Set(8, _world.Top + 1, 8, mold);
        var stack = HoldCrucible(800);

        Assert.True(Click(new Vector3(8.5f, _world.Top + 1.2f, 8.5f), out _));

        var state = _world.World.Casting.StateAt(8, _world.Top + 1, 8);
        Assert.NotNull(state);
        Assert.Equal(stack.InstanceId, state!.PourInstance);

        var inventory = _world.Player.Inventory;
        _world.World.Casting.Update(1f, _world.Manager, new DropManager(), inventory, inventory.SelectedToolInstance,
            _world.Player.Context.Eye, World.InteractionReach);
        Assert.True(state.FilledMl > 0);
        Assert.True(stack.Get<CrucibleComponent>().MoltenMl < 800);
    }

    [Theory]
    [InlineData(3f, 0f)]
    [InlineData(3f, 2f)]
    [InlineData(-2.5f, 3f)]
    public void AMoldAimedAtFromTheSideTakesThePourToo(float dx, float dz)
    {
        var mold = ContentRegistry.RequireBlock("fired_mold_ingot").Id;
        _world.Load(8, _world.Top, 8);
        _world.Set(8, _world.Top + 1, 8, mold);
        HoldCrucible(800);

        var eye = _world.Player.Context.Eye;
        _world.Player.Context.Eye = new Vector3(8.5f + dx, eye.Y, 8.5f + dz);
        Assert.True(Click(new Vector3(8.5f, _world.Top + 1.1f, 8.5f), out _));

        Assert.NotNull(_world.World.Casting.StateAt(8, _world.Top + 1, 8));
    }

    private (int X, int Y, int Z) Stand => (8, _world.Top + 1, 8);

    private void StandCrucibleAt(int x, int y, int z, int moltenMl)
    {
        var stack = new ItemStack(0, ContentRegistry.RequireItem("crucible").Id);
        stack.Set(new CrucibleComponent(Copper, moltenMl, 0));
        _world.Set(x, y, z, ContentRegistry.RequireBlock(StandingCrucibleManager.StandBlock).Id);
        _world.World.StandingCrucibles.Place((x, y, z), stack);
    }

    [Fact]
    public void LiftingAStandingCrucibleKeepsItsMetalAndPutsItInTheHand()
    {
        _world.Load(8, _world.Top, 8);
        var (x, y, z) = Stand;
        StandCrucibleAt(x, y, z, 800);

        Assert.True(Click(new Vector3(x + 0.5f, y + 0.25f, z + 0.5f), out _));

        var inventory = _world.Player.Inventory;
        Assert.IsType<CrucibleItem>(inventory.HeldItem);
        Assert.Equal(800, inventory.GetToolStack(inventory.SelectedToolInstance)!.Get<CrucibleComponent>().MoltenMl);
        Assert.Equal(0, _world.IdAt(x, y, z));
    }

    [Fact]
    public void ALiftedBlockLandsInTheHandWithoutMovingTheSelection()
    {
        var inventory = _world.Player.Inventory;
        inventory.Select(3);
        var crucible = new ItemStack(0, ContentRegistry.RequireItem("crucible").Id);

        Assert.True(inventory.AddLifted(crucible));

        Assert.Equal(3, inventory.SelectedIndex);
        Assert.IsType<CrucibleItem>(inventory.HeldItem);
    }

    [Fact]
    public void ACrucibleStandsOnAFullBlockFaceButNotOnAMold()
    {
        var mold = ContentRegistry.RequireBlock("fired_mold_ingot").Id;
        _world.Load(8, _world.Top, 8);
        _world.Set(8, _world.Top + 1, 8, mold);
        // The slab's top is not the top of its cell, so nothing stands on it; a full block takes the stand
        Assert.False(_world.World.Placer.PlaceBlockAtFace(new Vector3(8, _world.Top + 1, 8), Vector3.UnitY,
            ContentRegistry.RequireBlock(StandingCrucibleManager.StandBlock).Id, 0, Vector3.UnitZ, out _));
        Assert.True(_world.World.Placer.PlaceBlockAtFace(new Vector3(4, _world.Top, 4), Vector3.UnitY,
            ContentRegistry.RequireBlock(StandingCrucibleManager.StandBlock).Id, 0, Vector3.UnitZ, out _));
    }

    [Fact]
    public void AMoldAtNegativeCoordinatesPoursAndDrainsToo()
    {
        var mold = ContentRegistry.RequireBlock("fired_mold_ingot").Id;
        var y = _world.Top + 1;
        _world.Load(-5, _world.Top, -5);
        _world.Set(-5, y, -5, mold);
        var stack = HoldCrucible(800);
        var eye = _world.Player.Context.Eye;
        _world.Player.Context.Eye = new Vector3(-4.5f, eye.Y, -4.5f);

        Assert.True(Click(new Vector3(-4.5f, y + 0.2f, -4.5f), out _));
        var state = _world.World.Casting.StateAt(-5, y, -5);
        Assert.NotNull(state);

        var inventory = _world.Player.Inventory;
        _world.World.Casting.Update(1f, _world.Manager, new DropManager(), inventory, inventory.SelectedToolInstance,
            _world.Player.Context.Eye, World.InteractionReach);
        Assert.True(state!.FilledMl > 0);
        Assert.True(stack.Get<CrucibleComponent>().MoltenMl < 800);
    }
}
