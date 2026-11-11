using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Interaction;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// The right-click order: a block the crosshair is on (here a door) claims the click before a held item's timed use
/// (a bandage) starts charging, and the timed use charges when no block claims the click.
/// </summary>
[Collection("Worldgen seed")]
public sealed class RightClickPriorityTests : IDisposable
{
    private readonly SupportTestWorld _world;

    public RightClickPriorityTests()
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

    // A closed door on the plateau and a bandage in hand, the eye in front of it
    private (int X, int Y, int Z) DoorAndBandage()
    {
        var door = BlockType.Types.Values.First(t => t.UpperHalfId != 0 && t.Openable);
        _world.Load(4, _world.Top, 4);
        Assert.True(_world.Place(door.Id, new Vector3(4, _world.Top, 4), Vector3.UnitY));
        var bandage = ContentRegistry.RequireItem("bandage");
        Assert.True(bandage.UseSeconds > 0f);
        _world.Player.Inventory.Add(bandage.Id);
        Assert.True(_world.Player.Inventory.PickBlock(bandage.Id));
        return (4, _world.Top + 1, 4);
    }

    private bool Click(out RightClickOutcome outcome) =>
        _world.World.RightClick.TryUseSelected(World.InteractionReach, _world.Player.Context, out outcome, beginTimedUse: true);

    [Fact]
    public void ADoorUnderTheCrosshairOpensInsteadOfStartingTheBandage()
    {
        var (x, y, z) = DoorAndBandage();
        var eye = _world.Player.Context.Eye;
        _world.Player.Aim(eye, Vector3.Normalize(new Vector3(x, y, z) + new Vector3(0.5f, 0.5f, 0.5f) - eye));

        Assert.True(Click(out var outcome));
        Assert.Equal(RightClickOutcome.Station, outcome);
        Assert.True(DoorState.IsOpen(_world.StateAt(x, y, z)));
        Assert.False(_world.World.RightClick.IsUseCharging);
    }

    [Fact]
    public void WithNoBlockToClaimItTheBandageStartsCharging()
    {
        DoorAndBandage();
        _world.Player.Aim(_world.Player.Context.Eye, Vector3.UnitY);

        Assert.True(Click(out var outcome));
        Assert.Equal(RightClickOutcome.UseCharge, outcome);
        Assert.True(_world.World.RightClick.IsUseCharging);
    }
}
