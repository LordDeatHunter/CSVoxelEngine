using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Interaction;
using VoxelEngine.Simulation.Smithing;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// The iron works in a live world: a cube of bloomery bricks converts into a tracked bloomery through the world's own
/// convert path, mining it hands back what it held, a bloomery and an anvil survive the world's save snapshot, a
/// hammer hit that finishes a bloom drops the iron and the slag on top of the anvil, and right-clicks stack that iron on
/// the anvil and take it back.
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
    public void AHammerHitThatFinishesABloomDropsTheIronAndTheSlagOnTheAnvil()
    {
        _world = new SupportTestWorld();
        var anvilBlock = BlockType.Types.Values.First(b => b.Anvil != null);
        var cell = new Vector3(2, _world.Top + 1, 2);
        _world.Load(3, _world.Top + 2, 3);
        _world.Set((int)cell.X, (int)cell.Y, (int)cell.Z, anvilBlock.Id);

        var profile = anvilBlock.Anvil!;
        Assert.True(_world.World.Interactions.TryTrackAnvilAt(cell));
        Assert.True(_world.World.Smithing.Anvils.TryPlace(cell, profile.InputId));

        for (var hit = 1; hit < profile.Hits; hit++)
            Assert.Equal(AnvilHitKind.Struck, _world.World.HammerFor(cell, 0f, 0f).Kind);
        Assert.Empty(_world.World.Drops);

        Assert.Equal(AnvilHitKind.Finished, _world.World.HammerFor(cell, 0f, 0f).Kind);
        Assert.Equal(BloomeryConfig.IronPerBloom, _world.World.Drops.Count(d => d.BlockId == profile.ResultId));
        Assert.Equal(BloomeryConfig.SlagPerBloom, _world.World.Drops.Count(d => d.BlockId == profile.SlagId));
        Assert.All(_world.World.Drops, d => Assert.True(d.Position.Y > cell.Y + 0.5f));
    }

    [Fact]
    public void RightClicksStackTheMetalTakeOneBackOrAllWhileSneakingAndAHammerOpensTheRecipes()
    {
        _world = new SupportTestWorld();
        var anvilBlock = BlockType.Types.Values.First(b => b.Anvil != null);
        var (x, y, z) = (8, _world.Top + 1, 6);
        _world.Load(x, y, z);
        _world.Set(x, y, z, anvilBlock.Id);
        var cell = new Vector3(x, y, z);

        var player = _world.Player;
        var inventory = player.Inventory;
        var metal = anvilBlock.Anvil!.ResultId;
        var coal = ContentRegistry.RequireItem("coal").Id;
        var hammer = ContentRegistry.RequireItem("smithing_hammer").Id;
        Assert.True(inventory.Add(coal));
        Assert.True(inventory.Add(metal, 3));
        var eye = player.Context.Eye;
        player.Aim(eye, Vector3.Normalize(cell + new Vector3(0.5f, 0.9f, 0.5f) - eye));
        bool Click() => _world.World.Interactions.TryAnvilInteract(World.InteractionReach, player.Context);
        bool Hold(ushort id) => inventory.PickBlock(id) && inventory.HeldItem?.Id == id;

        // Each click with the metal in hand lays one more on the stack
        Assert.True(Hold(metal));
        for (var i = 0; i < 3; i++)
            Assert.True(Click());
        var state = _world.World.Smithing.Anvils.StateAt(x, y, z)!;
        Assert.Equal((metal, 3), (state.MetalId, state.MetalCount));
        Assert.Equal(0, inventory.CountFor(metal));

        // Anything else in hand takes one back
        Assert.True(Hold(coal));
        Assert.True(Click());
        Assert.Equal((2, 1), (state.MetalCount, inventory.CountFor(metal)));

        // A hammer never takes or lays anything: it opens the recipe list on a stack
        Assert.True(inventory.Add(hammer));
        Assert.True(Hold(hammer));
        Assert.False(Click());
        Assert.True(_world.World.Interactions.TryOpenForge(World.InteractionReach, player.Context, out var forgeCell));
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
        Assert.False(_world.World.Interactions.TryOpenForge(World.InteractionReach, player.Context, out _));
    }

    [Fact]
    public void ABloomeryAndAnAnvilWithAPieceSurviveTheWorldsSaveSnapshot()
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
