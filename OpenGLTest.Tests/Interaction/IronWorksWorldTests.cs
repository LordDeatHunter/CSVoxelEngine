using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Interaction;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Smithing;
using VoxelEngine.Simulation.WorkStations;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// The iron works in a live world: a cube of bloomery bricks converts into a tracked bloomery through the world's own
/// convert path, mining it hands back what it held, a landing on a bellows at its tuyere heats it, a bloomery survives
/// the world's save snapshot, the hammer hit that finishes a picked recipe drops its result and byproduct on top of the
/// anvil, and right-clicks stack blooms on the anvil and take them back.
/// </summary>
[Collection("Worldgen seed")]
public sealed class IronWorksWorldTests : IDisposable
{
    private SupportTestWorld? _world;

    public IronWorksWorldTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    public void Dispose() => _world?.Dispose();

    private static BlockType Bloomery => ContentRegistry.RequireBlock("bloomery");

    private Vector3 BuildBloomery()
    {
        _world = new SupportTestWorld();
        var anchor = new Vector3(2, _world.Top + 1, 2);
        _world.Load(3, _world.Top + 2, 3);
        var size = Bloomery.MultiblockSize;
        foreach (var (dx, dy, dz) in Multiblock.Offsets(size))
            _world.Set((int)anchor.X + dx, (int)anchor.Y + dy, (int)anchor.Z + dz, Bloomery.MultiblockSourceId);

        var clicked = anchor + new Vector3(0, 0, size - 1);
        Assert.True(_world.World.Edits.TryConvertMultiblock(clicked, Vector3.UnitZ, Bloomery.MultiblockSourceId,
            Bloomery.Id, -Vector3.UnitZ));
        return anchor;
    }

    [Fact]
    public void ConvertingTracksTheBloomeryAndMiningItHandsBackWhatItHeld()
    {
        var anchor = BuildBloomery();
        var bloomeries = _world!.World.Smithing.Bloomeries;
        var state = bloomeries.StateAt((int)anchor.X, (int)anchor.Y, (int)anchor.Z);
        Assert.NotNull(state);

        var profile = Bloomery.Bloomery!;
        Assert.True(bloomeries.TryAddOre(anchor, profile.OreId));
        Assert.True(bloomeries.TryAddOre(anchor, profile.OreId));
        Assert.True(bloomeries.TryAddFuel(anchor, ContentRegistry.RequireItem("coal")));

        var mined = anchor + new Vector3(Bloomery.MultiblockSize - 1, 0, Bloomery.MultiblockSize - 1);
        Assert.True(_world.Break((int)mined.X, (int)mined.Y, (int)mined.Z));

        Assert.Null(bloomeries.StateAt((int)anchor.X, (int)anchor.Y, (int)anchor.Z));
        Assert.Equal(2, _world.World.Drops.Count(d => d.BlockId == profile.OreId));
        Assert.Equal(1, _world.World.Drops.Count(d => d.BlockId == ContentRegistry.RequireItem("coal").Id));
    }

    [Fact]
    public void ALandingOnABellowsAtATuyereHeatsTheLitBloomeryAndALandingBesideItDoesNot()
    {
        var anchor = BuildBloomery();
        var bloomeries = _world!.World.Smithing.Bloomeries;
        var state = bloomeries.StateAt((int)anchor.X, (int)anchor.Y, (int)anchor.Z)!;
        Assert.True(bloomeries.TryAddOre(anchor, Bloomery.Bloomery!.OreId));
        Assert.True(bloomeries.TryAddFuel(anchor, ContentRegistry.RequireItem("coal")));
        Assert.True(bloomeries.Ignite(anchor));

        // A bellows on the first tuyere cell of the structure as it was built
        var size = Bloomery.MultiblockSize;
        var tuyere = Enumerable.Range(-1, size + 2)
            .SelectMany(dx => Enumerable.Range(-1, size + 2).Select(dz => (dx, dz)))
            .Select(d => (d.dx, 0, d.dz))
            .First(d => Bloomery.Bloomery.IsTuyere(d, state.Facing, size));
        var bellows = new Vector3(anchor.X + tuyere.dx, anchor.Y, anchor.Z + tuyere.dz);
        _world.Set((int)bellows.X, (int)bellows.Y, (int)bellows.Z, BlockType.Types.Values.First(b => b.IsBellows).Id);
        var feetOnTop = bellows + new Vector3(0.5f, 1f, 0.5f);

        state.Heat = 0.2f;
        // Landing on the ground beside it (two cells off the bellows, away from the structure) heats nothing
        var away = Vector3.Normalize(new Vector3(tuyere.dx - (size - 1) * 0.5f, 0f, tuyere.dz - (size - 1) * 0.5f));
        Assert.False(_world.World.PressBellows(feetOnTop + away * 2f, 0.3f, 1.2f));
        Assert.Equal(0.2f, state.Heat);

        Assert.True(_world.World.PressBellows(feetOnTop, 0.3f, 1.2f));
        Assert.True(state.Heat > 0.2f);
    }

    [Fact]
    public void ThePickedRecipesLastHammerHitDropsItsResultAndByproductOnTheAnvil()
    {
        _world = new SupportTestWorld();
        var anvilBlock = ContentRegistry.RequireBlock("anvil");
        var cell = new Vector3(2, _world.Top + 1, 2);
        _world.Load(3, _world.Top + 2, 3);
        _world.Set((int)cell.X, (int)cell.Y, (int)cell.Z, anvilBlock.Id);

        var recipe = ContentRegistry.WorkRecipes.First(r => r.InputId == Bloomery.Bloomery!.BloomId && r.ByproductId != 0);
        var anvils = _world.World.Smithing.WorkStations;
        Assert.True(_world.World.Interactions.TryTrackWorkStationAt(cell, out _));
        for (var i = 0; i < recipe.InputCount; i++)
            Assert.True(anvils.TryStack(cell, recipe.InputId));
        Assert.True(_world.World.Smithing.Select(cell, recipe.ResultId));

        for (var hit = 1; hit < recipe.Hits; hit++)
            Assert.Equal(WorkHitKind.Struck, _world.World.WorkStationHitFor(cell, 0f, 0f).Kind);
        Assert.Empty(_world.World.Drops);

        Assert.Equal(WorkHitKind.Finished, _world.World.WorkStationHitFor(cell, 0f, 0f).Kind);
        Assert.Equal(recipe.ResultCount, _world.World.Drops.Count(d => d.BlockId == recipe.ResultId));
        Assert.Equal(recipe.ByproductCount, _world.World.Drops.Count(d => d.BlockId == recipe.ByproductId));
        Assert.All(_world.World.Drops, d => Assert.True(d.Position.Y > cell.Y + 0.5f));
        Assert.True(anvils.StateAt((int)cell.X, (int)cell.Y, (int)cell.Z)!.IsEmpty);
    }

    // An anvil with a recipe picked, the hammer in hand and the eye looking at it from two blocks off
    private (Vector3 Cell, WorkStationManager.State State) AnvilUnderTheHammer()
    {
        _world = new SupportTestWorld();
        var cell = new Vector3(2, _world.Top + 1, 2);
        _world.Load(3, _world.Top + 2, 5);
        _world.Set((int)cell.X, (int)cell.Y, (int)cell.Z, ContentRegistry.RequireBlock("anvil").Id);

        var recipe = ContentRegistry.WorkRecipes.First(r => r.InputId == Bloomery.Bloomery!.BloomId && r.Hits > 1);
        var world = _world.World;
        Assert.True(world.Interactions.TryTrackWorkStationAt(cell, out _));
        for (var i = 0; i < recipe.InputCount; i++)
            Assert.True(world.Smithing.WorkStations.TryStack(cell, recipe.InputId));
        Assert.True(world.Smithing.Select(cell, recipe.ResultId));

        var hammer = ContentRegistry.RequireItem("bronze_hammer").Id;
        Assert.True(world.Inventory.Add(hammer));
        Assert.True(world.Inventory.PickBlock(hammer));
        world.Camera.TestTeleport(cell + new Vector3(0.5f, 0.5f, 2.5f), yaw: -90f, pitch: 0f);
        return (cell, world.Smithing.WorkStations.StateAt((int)cell.X, (int)cell.Y, (int)cell.Z)!);
    }

    private void Swing(int frames, bool holding)
    {
        for (var i = 0; i < frames; i++)
        {
            _world!.World.LeftClick.UpdateSelection();
            _world.World.LeftClick.UpdateBlockBreaking(1f / 60f, holding);
        }
    }

    [Fact]
    public void AHammerBlowWindsUpAndLandsItsHitWhenTheHammerComesDownEvenWithTheButtonReleased()
    {
        var (_, state) = AnvilUnderTheHammer();
        var model = _world!.World.Player;
        var lead = PlayerModel.SwordDuration(SwordSwing.HammerStrike) * PlayerModel.SwordImpactProgress(SwordSwing.HammerStrike);

        // The press starts the blow, and nothing is struck while the hammer is still on its way up
        Swing(1, holding: true);
        Assert.True(model.IsSwordSwinging);
        Assert.Equal(SwordSwing.HammerStrike, model.ActiveSwordSwing);
        Swing((int)(lead * 60f) - 3, holding: false);
        Assert.Equal(0, state.Hits);
        Assert.True(_world.World.LeftClick.IsBlowInFlight);

        // It comes down on its own: one hit, and the blow is spent
        Swing(6, holding: false);
        Assert.Equal(1, state.Hits);
        Assert.False(_world.World.LeftClick.IsBlowInFlight);
    }

    [Fact]
    public void AHammerBlowWhoseAimLeftTheAnvilComesDownOnNothing()
    {
        var (cell, state) = AnvilUnderTheHammer();
        Swing(1, holding: true);
        Assert.True(_world!.World.LeftClick.IsBlowInFlight);

        // Looking straight up while the hammer is raised
        _world.World.Camera.TestTeleport(cell + new Vector3(0.5f, 0.5f, 2.5f), yaw: -90f, pitch: 89f);
        Swing(120, holding: false);
        Assert.False(_world.World.LeftClick.IsBlowInFlight);
        Assert.Equal(0, state.Hits);
    }

    [Fact]
    public void RightClicksStackTheMetalTakeOneBackOrAllWhileSneakingAndAHammerOpensTheRecipes()
    {
        _world = new SupportTestWorld();
        var anvilBlock = ContentRegistry.RequireBlock("anvil");
        var (x, y, z) = (8, _world.Top + 1, 6);
        _world.Load(x, y, z);
        _world.Set(x, y, z, anvilBlock.Id);
        var cell = new Vector3(x, y, z);

        var player = _world.Player;
        var inventory = player.Inventory;
        var metal = Bloomery.Bloomery!.BloomId;
        var coal = ContentRegistry.RequireItem("coal").Id;
        var hammer = ContentRegistry.RequireItem("bronze_hammer").Id;
        Assert.True(inventory.Add(coal));
        Assert.True(inventory.Add(metal, 3));
        var eye = player.Context.Eye;
        player.Aim(eye, Vector3.Normalize(cell + new Vector3(0.5f, 0.9f, 0.5f) - eye));
        bool Click() => _world.World.Interactions.TryWorkStationInteract(World.InteractionReach, player.Context);
        bool Hold(ushort id) => inventory.PickBlock(id) && inventory.HeldItem?.Id == id;

        // Each click with the metal in hand lays one more on the stack
        Assert.True(Hold(metal));
        for (var i = 0; i < 3; i++)
            Assert.True(Click());
        var state = _world.World.Smithing.WorkStations.StateAt(x, y, z)!;
        Assert.Equal((metal, 3), (state.InputId, state.InputCount));
        Assert.Equal(0, inventory.CountFor(metal));

        // Anything else in hand takes one back
        Assert.True(Hold(coal));
        Assert.True(Click());
        Assert.Equal((2, 1), (state.InputCount, inventory.CountFor(metal)));

        // A hammer never takes or lays anything: it opens the recipe list on a stack
        Assert.True(inventory.Add(hammer));
        Assert.True(Hold(hammer));
        Assert.False(Click());
        Assert.True(_world.World.Interactions.TryOpenWorkList(World.InteractionReach, player.Context, out var forgeCell));
        Assert.Equal(cell, forgeCell);

        // ...and while sneaking the whole stack comes back
        Assert.True(Hold(coal));
        player.Context.RemoteSneaking = true;
        Assert.True(Click());
        Assert.True(state.IsEmpty);
        Assert.Equal(3, inventory.CountFor(metal));

        // An empty anvil does not claim the click, so a block still places against it, and a hammer opens nothing
        Assert.False(Click());
        Assert.True(Hold(hammer));
        Assert.False(_world.World.Interactions.TryOpenWorkList(World.InteractionReach, player.Context, out _));
    }

    [Fact]
    public void ABloomerySurvivesTheWorldsSaveSnapshot()
    {
        var anchor = BuildBloomery();
        var smithing = _world!.World.Smithing;
        Assert.True(smithing.Bloomeries.TryAddOre(anchor, Bloomery.Bloomery!.OreId));

        var data = new VoxelEngine.Saves.StationSaveData();
        smithing.Capture(data);
        var restored = new SmithingStations();
        Assert.Equal(0, restored.Restore(data));

        var back = restored.Bloomeries.StateAt((int)anchor.X, (int)anchor.Y, (int)anchor.Z);
        Assert.NotNull(back);
        Assert.Equal(1, back!.Ore);
    }
}
