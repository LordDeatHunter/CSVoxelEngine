using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class CampfireCookingTests
{
    public CampfireCookingTests() => TestContent.EnsureLoaded();

    private static byte Mudball => ContentRegistry.RequireItem("mudball").Id;
    private static byte MudBrick => ContentRegistry.RequireItem("mud_brick").Id;
    private static byte BranchPile => ContentRegistry.RequireBlock("branch_pile").Id;

    private static ChunkManager LitPile(out CampfireManager fires, out DropManager drops, byte state = 4)
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;
        chunk.SetBlockRaw(0, 0, 0, BranchPile, state);

        fires = new CampfireManager();
        drops = new DropManager();
        Assert.True(fires.Ignite(0, 0, 0));
        return manager;
    }

    [Fact]
    public void NonCookableHasNoRecipe()
    {
        var branch = ContentRegistry.RequireItem("branch").Id;
        Assert.Null(ContentRegistry.CookRecipeForInput(branch));
    }

    [Fact]
    public void PlacingFoodNeedsALitFire()
    {
        var fires = new CampfireManager();
        Assert.False(fires.TryPlaceFood(0, 0, 0, Mudball));
        Assert.Null(fires.CookingAt(0, 0, 0));
    }

    [Fact]
    public void PlacingFoodTakesASlot()
    {
        var manager = LitPile(out var fires, out _);
        Assert.True(fires.TryPlaceFood(0, 0, 0, Mudball));
        var slots = Assert.Single(fires.CookingAt(0, 0, 0)!);
        Assert.Equal(Mudball, slots.InputId);
        Assert.Equal(MudBrick, slots.OutputId);
        Assert.False(slots.IsDone);
    }

    [Fact]
    public void FourSlotsMax()
    {
        LitPile(out var fires, out _);
        for (var i = 0; i < CampfireManager.MaxCookSlots; i++)
            Assert.True(fires.TryPlaceFood(0, 0, 0, Mudball));
        Assert.False(fires.TryPlaceFood(0, 0, 0, Mudball));
        Assert.Equal(CampfireManager.MaxCookSlots, fires.CookingAt(0, 0, 0)!.Count);
    }

    [Fact]
    public void CookingFinishesIntoOutputAndStaysForPickup()
    {
        var manager = LitPile(out var fires, out var drops);
        Assert.True(fires.TryPlaceFood(0, 0, 0, Mudball));
        fires.Update(12f, manager, drops);
        var slot = Assert.Single(fires.CookingAt(0, 0, 0)!);
        Assert.True(slot.IsDone);
        var view = Assert.Single(fires.CookingView().ToArray());
        Assert.Equal(MudBrick, view.ItemId);
        Assert.True(view.Done);
        Assert.Empty(drops.Drops);

        var collected = fires.CollectFinished(0, 0, 0);
        Assert.Equal([MudBrick], collected);
        Assert.Null(fires.CookingAt(0, 0, 0));
    }

    [Fact]
    public void ExtinguishDropsUncookedInput()
    {
        var manager = LitPile(out var fires, out var drops, state: 1);
        fires.Update(CampfireManager.BurnSeconds - 5f, manager, drops);
        Assert.True(fires.TryPlaceFood(0, 0, 0, Mudball));
        fires.Update(CampfireManager.BurnSeconds, manager, drops);
        Assert.False(fires.IsLit(0, 0, 0));
        Assert.Contains(drops.Drops, d => d.BlockId == Mudball);
        Assert.DoesNotContain(drops.Drops, d => d.BlockId == MudBrick);
    }

    [Fact]
    public void FinishedOutputDropsOnExtinguish()
    {
        var manager = LitPile(out var fires, out var drops);
        Assert.True(fires.TryPlaceFood(0, 0, 0, Mudball));
        fires.Update(12f, manager, drops);
        Assert.True(fires.CookingAt(0, 0, 0)![0].IsDone);
        for (var i = 0; i < 4; i++)
            fires.Update(CampfireManager.BurnSeconds, manager, drops);
        Assert.False(fires.IsLit(0, 0, 0));
        Assert.Contains(drops.Drops, d => d.BlockId == MudBrick);
    }

    [Fact]
    public void ManualBreakDropsUncookedInput()
    {
        LitPile(out var fires, out var drops);
        Assert.True(fires.TryPlaceFood(0, 0, 0, Mudball));
        fires.NotifyBlockRemoved(0, 0, 0, drops);
        Assert.Contains(drops.Drops, d => d.BlockId == Mudball);
        Assert.Null(fires.CookingAt(0, 0, 0));
    }
}
