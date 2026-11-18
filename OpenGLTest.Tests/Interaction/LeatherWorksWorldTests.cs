using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Simulation.Leatherwork;
using VoxelEngine.Simulation.WorkStations;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// The leather works in a live world: raw hides laid on a fleshing beam with right-clicks are scraped by a knife's
/// strokes, a scraped hide stays on the beam (no drop, no take-back) until a right-click with salt spends one salt and
/// hands it over as a salted hide, and a knife's right-click never lays or takes anything; a tanning vat filled from a
/// bucket (which comes back empty) tans its hides in the world's frame and an empty hand collects the leather.
/// </summary>
[Collection("Worldgen seed")]
public sealed class LeatherWorksWorldTests : IDisposable
{
    private SupportTestWorld? _world;

    public LeatherWorksWorldTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    public void Dispose() => _world?.Dispose();

    [Fact]
    public void AScrapedHideWaitsOnTheBeamUntilSaltTakesItOffSalted()
    {
        _world = new SupportTestWorld();
        var beam = ContentRegistry.RequireBlock("fleshing_beam");
        var recipe = ContentRegistry.WorkRecipes.First(r => r.Station == beam.WorkStation!.Recipes);
        var (x, y, z) = (8, _world.Top + 1, 6);
        _world.Load(x, y, z);
        _world.Set(x, y, z, beam.Id);
        var cell = new Vector3(x, y, z);

        var player = _world.Player;
        var inventory = player.Inventory;
        var knife = ContentRegistry.Tags[beam.WorkStation!.ToolTag].First();
        Assert.True(inventory.Add(recipe.InputId, recipe.InputCount));
        Assert.True(inventory.Add(recipe.CollectWithId, 2));
        Assert.True(inventory.Add(knife));
        var eye = player.Context.Eye;
        player.Aim(eye, Vector3.Normalize(cell + new Vector3(0.5f, 0.6f, 0.5f) - eye));
        bool Click() => _world.World.Interactions.TryWorkStationInteract(World.InteractionReach, player.Context);
        bool Hold(ushort id) => inventory.PickBlock(id) && inventory.HeldItem?.Id == id;

        Assert.True(Hold(recipe.InputId));
        for (var i = 0; i < recipe.InputCount; i++)
            Assert.True(Click());
        var state = _world.World.Smithing.WorkStations.StateAt(x, y, z)!;
        Assert.Equal(recipe.InputCount, state.InputCount);

        // The knife lays and takes nothing; its strokes scrape the one recipe the beam offers, no pick needed
        Assert.True(Hold(knife));
        Assert.False(Click());
        for (var hit = 1; hit < recipe.Hits; hit++)
            Assert.Equal(WorkHitKind.Struck, _world.World.WorkStationHitFor(cell, 0f, 0f).Kind);
        Assert.Equal(WorkHitKind.Finished, _world.World.WorkStationHitFor(cell, 0f, 0f).Kind);

        // The scraped hide neither drops nor comes back to another item in hand
        Assert.DoesNotContain(_world.World.Drops, d => d.BlockId == recipe.ResultId);
        Assert.Equal(recipe.ResultCount, state.FinishedCount);
        var coal = ContentRegistry.RequireItem("coal").Id;
        Assert.True(inventory.Add(coal));
        Assert.True(Hold(coal));
        Assert.False(Click());
        Assert.Equal(recipe.ResultCount, state.FinishedCount);

        // Salt takes it off salted, one salt a hide
        Assert.True(Hold(recipe.CollectWithId));
        Assert.True(Click());
        Assert.Equal(1, inventory.CountFor(recipe.ResultId));
        Assert.Equal(1, inventory.CountFor(recipe.CollectWithId));
        Assert.Equal(recipe.ResultCount - 1, state.FinishedCount);
    }
    [Fact]
    public void AVatFilledFromABucketTansItsHidesAndAnEmptyHandCollectsTheLeather()
    {
        _world = new SupportTestWorld();
        var vat = ContentRegistry.RequireBlock("tanning_vat");
        var profile = vat.Tanning!;
        var (x, y, z) = (8, _world.Top + 1, 6);
        _world.Load(x, y, z);
        _world.Set(x, y, z, vat.Id);
        var cell = new Vector3(x, y, z);

        var player = _world.Player;
        var inventory = player.Inventory;
        var bucket = ContentRegistry.RequireItem("water_bucket");
        Assert.Equal(profile.WaterId, bucket.Id);
        Assert.True(inventory.Add(bucket.Id));
        Assert.True(inventory.Add(profile.TanninId, 2));
        Assert.True(inventory.Add(profile.InputId, 2));
        var eye = player.Context.Eye;
        player.Aim(eye, Vector3.Normalize(cell + new Vector3(0.5f, 0.6f, 0.5f) - eye));
        bool Click() => _world.World.Interactions.TryVatInteract(World.InteractionReach, player.Context);
        bool Hold(ushort id) => inventory.PickBlock(id) && inventory.HeldItem?.Id == id;

        // The water goes in and the emptied bucket comes back to the hand
        Assert.True(Hold(bucket.Id));
        Assert.True(Click());
        Assert.Equal(0, inventory.CountFor(bucket.Id));
        Assert.Equal(1, inventory.CountFor(bucket.UseResultId));

        Assert.True(Hold(profile.TanninId));
        Assert.True(Click());
        Assert.True(Click());
        Assert.True(Hold(profile.InputId));
        Assert.True(Click());
        Assert.True(Click());
        var state = _world.World.Smithing.Vats.StateAt(x, y, z)!;
        Assert.True(TanningRules.CanTan(state));

        // The world's own frame tans it
        for (var t = 0f; t <= profile.Seconds + 1f; t += 0.5f)
            _world.World.Smithing.Update(0.5f, _world.World.GetChunkManager(), _world.World.DropSpawner, null);
        Assert.Equal(2 * profile.ResultPerInput, state.Leather);

        var coal = ContentRegistry.RequireItem("coal").Id;
        Assert.True(inventory.Add(coal));
        Assert.True(Hold(coal));
        Assert.True(Click());
        Assert.Equal(2 * profile.ResultPerInput, inventory.CountFor(profile.ResultId));
        Assert.True(state.IsEmpty);
    }
}
