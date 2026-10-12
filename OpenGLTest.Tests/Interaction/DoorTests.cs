using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Interaction;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// Two-cell blocks (the door, <see cref="TallBlock"/>): the lower half is the item and the drop,
/// placement needs both cells and writes both with one state, breaking either takes the other down
/// and drops one door, and a click on either half swings both.
/// </summary>
[Collection("Worldgen seed")]
public sealed class DoorTests : IDisposable
{
    private SupportTestWorld? _world;

    public DoorTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    public void Dispose()
    {
        _world?.Dispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static BlockType[] Lowers() => BlockType.Types.Values.Where(t => t.UpperHalfId != 0).ToArray();

    private static BlockType Door() => Lowers().First(t => t.Openable);

    [Fact]
    public void ATwoCellBlockPairsAHeldLowerHalfWithAnUpperHalfPickedAsIt()
    {
        var lowers = Lowers();
        Assert.NotEmpty(lowers);
        var cell = new Vector3(2, 5, -3);
        foreach (var lower in lowers)
        {
            var upper = BlockType.ById[lower.UpperHalfId]!;
            Assert.Equal(lower.Id, upper.LowerHalfId);
            Assert.True(lower.HasItem);
            Assert.False(upper.HasItem);
            Assert.Equal((lower.HasHorizontalFacing, lower.Openable), (upper.HasHorizontalFacing, upper.Openable));
            Assert.Equal(lower.Id, BlockType.PickId(upper.Id));
            Assert.Equal(lower.Id, TallBlock.LowerOf(upper.Id));
            Assert.Equal(lower.Id, TallBlock.LowerOf(lower.Id));

            Assert.True(TallBlock.TryGetPartner(lower.Id, cell, out var above, out var aboveId));
            Assert.Equal((cell + Vector3.UnitY, upper.Id), (above, aboveId));
            Assert.True(TallBlock.TryGetPartner(upper.Id, above, out var below, out var belowId));
            Assert.Equal((cell, lower.Id), (below, belowId));
        }

        Assert.False(TallBlock.TryGetPartner(ContentRegistry.RequireBlock("stone").Id, cell, out _, out _));
    }

    private (int X, int Y, int Z) PlaceDoor(byte door, int x, int z, int facing)
    {
        _world!.Player.Context.ViewDirection = Chunk.CubeFaces[facing].normal;
        Assert.True(_world.Place(door, new Vector3(x, _world.Top, z), Vector3.UnitY));
        return (x, _world.Top + 1, z);
    }

    [Fact]
    public void PlacingADoorFillsBothCellsWithOneSharedState()
    {
        _world = new SupportTestWorld();
        var door = Door();
        _world.Load(4, _world.Top, 4);

        for (var facing = 0; facing < 4; facing++)
        {
            var held = _world.Player.Inventory.CountFor(door.Id);
            var (x, y, z) = PlaceDoor(door.Id, 2 + 3 * facing, 4, facing);
            Assert.Equal(held, _world.Player.Inventory.CountFor(door.Id));
            Assert.Equal((door.Id, door.UpperHalfId), (_world.IdAt(x, y, z), _world.IdAt(x, y + 1, z)));
            var state = _world.StateAt(x, y, z);
            Assert.Equal(state, _world.StateAt(x, y + 1, z));
            Assert.Equal((facing, false), (DoorState.Facing(state), DoorState.IsOpen(state)));
        }
    }

    [Fact]
    public void ADoorWithoutRoomForItsUpperHalfIsRefusedAndSpendsNothing()
    {
        _world = new SupportTestWorld();
        var door = Door();
        var (x, z) = (4, 4);
        _world.Load(x, _world.Top, z);
        _world.Set(x, _world.Top + 2, z, _world.Stone);

        var held = _world.Player.Inventory.CountFor(door.Id);
        Assert.False(_world.Place(door.Id, new Vector3(x, _world.Top, z), Vector3.UnitY));
        Assert.Equal(held + 1, _world.Player.Inventory.CountFor(door.Id));
        Assert.Equal(BlockType.Air, _world.IdAt(x, _world.Top + 1, z));
        Assert.Equal(_world.Stone, _world.IdAt(x, _world.Top + 2, z));
    }

    [Fact]
    public void BreakingEitherHalfTakesBothDownAndDropsOneDoor()
    {
        _world = new SupportTestWorld();
        var door = Door();
        _world.Load(4, _world.Top, 4);

        foreach (var upperFirst in new[] { true, false })
        {
            var (x, y, z) = PlaceDoor(door.Id, upperFirst ? 3 : 6, 4, 0);
            var dropsBefore = _world.World.Drops.Count(d => d.BlockId == door.Id);
            var allBefore = _world.World.Drops.Count;

            Assert.True(_world.Break(x, upperFirst ? y + 1 : y, z));
            Assert.Equal((BlockType.Air, BlockType.Air), (_world.IdAt(x, y, z), _world.IdAt(x, y + 1, z)));
            Assert.Equal(dropsBefore + 1, _world.World.Drops.Count(d => d.BlockId == door.Id));
            Assert.Equal(allBefore + 1, _world.World.Drops.Count);
        }
    }

    [Fact]
    public void AClickOnEitherHalfSwingsBoth()
    {
        _world = new SupportTestWorld();
        var door = Door();
        _world.Load(4, _world.Top, 4);
        var (x, y, z) = PlaceDoor(door.Id, 4, 4, 0);
        var eye = _world.Player.Context.Eye;

        // Click the upper half's closed panel: both halves open together
        ClickBoxes(x, y + 1, z);
        var opened = _world.StateAt(x, y, z);
        Assert.True(DoorState.IsOpen(opened));
        Assert.Equal(opened, _world.StateAt(x, y + 1, z));
        Assert.Equal(0, DoorState.Facing(opened));

        // Click the lower half, now hit as its open panel: both shut again
        ClickBoxes(x, y, z);
        var shut = _world.StateAt(x, y, z);
        Assert.False(DoorState.IsOpen(shut));
        Assert.Equal(shut, _world.StateAt(x, y + 1, z));

        void ClickBoxes(int cx, int cy, int cz)
        {
            var boxes = BlockShape.BoxesAt(_world!.IdAt(cx, cy, cz), _world.Manager, cx, cy, cz);
            var min = boxes.Select(b => b.Min).Aggregate(Vector3.Min);
            var max = boxes.Select(b => b.Max).Aggregate(Vector3.Max);
            _world.Player.Aim(eye, Vector3.Normalize(new Vector3(cx, cy, cz) + (min + max) * 0.5f - eye));
            Assert.True(_world.World.TryUseSelected(World.InteractionReach, _world.Player.Context, out var outcome));
            Assert.NotEqual(RightClickOutcome.Place, outcome);
        }
    }
}
