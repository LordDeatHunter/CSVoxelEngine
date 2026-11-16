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
/// and drops one door, and a click on either half swings both. Two doors whose free edges meet form a double
/// door (<see cref="DoubleDoor"/>): the second one hangs mirrored and a click on either swings both.
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
    public void ATwoCellBlockPairsALowerHalfWithAnUpperHalfPickedAsIt()
    {
        var lowers = Lowers();
        Assert.NotEmpty(lowers);
        var cell = new Vector3(2, 5, -3);
        foreach (var lower in lowers)
        {
            // Only the lower half is ever held (a door; a waystone pair has no item at all)
            var upper = BlockType.ById[lower.UpperHalfId]!;
            Assert.Equal(lower.Id, upper.LowerHalfId);
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

    private (int X, int Y, int Z) PlaceDoor(ushort door, int x, int z, int facing)
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
    }

    // Right-clicks the middle of the cell's current boxes from a spot right in front of them
    private void ClickBoxes(int cx, int cy, int cz)
    {
        var boxes = BlockShape.BoxesAt(_world!.IdAt(cx, cy, cz), _world.Manager, cx, cy, cz);
        var min = boxes.Select(b => b.Min).Aggregate(Vector3.Min);
        var max = boxes.Select(b => b.Max).Aggregate(Vector3.Max);
        var target = new Vector3(cx, cy, cz) + (min + max) * 0.5f;
        var eye = target + new Vector3(0.3f, 1.7f, 0.2f);
        _world.Player.Aim(eye, Vector3.Normalize(target - eye));
        Assert.True(_world.World.TryUseSelected(World.InteractionReach, _world.Player.Context, out var outcome));
        Assert.NotEqual(RightClickOutcome.Place, outcome);
    }

    private (int X, int Y, int Z) PlaceDoorAt(ushort door, Vector3 column, int facing) =>
        PlaceDoor(door, (int)column.X, (int)column.Z, facing);

    private bool PairedAt((int X, int Y, int Z) cell, out Vector3 partner) =>
        DoubleDoor.TryGetPartner(_world!.Manager, new Vector3(cell.X, cell.Y, cell.Z),
            _world.IdAt(cell.X, cell.Y, cell.Z), _world.StateAt(cell.X, cell.Y, cell.Z), out partner);

    [Fact]
    public void ADoorSetAgainstAnotherDoorsFreeEdgeHangsMirroredAndPairsWithIt()
    {
        _world = new SupportTestWorld();
        var door = Door();
        _world.Load(8, _world.Top, 8);

        for (var facing = 0; facing < 4; facing++)
        {
            var column = new Vector3(3 + 3 * facing, 0, 8);
            var first = PlaceDoorAt(door.Id, column, facing);
            var firstState = _world.StateAt(first.X, first.Y, first.Z);
            Assert.False(DoorState.HingeRight(firstState));
            Assert.False(PairedAt(first, out _));

            var second = PlaceDoorAt(door.Id, column + DoubleDoor.LatchSide(firstState), facing);
            var secondState = _world.StateAt(second.X, second.Y, second.Z);
            Assert.Equal((facing, true), (DoorState.Facing(secondState), DoorState.HingeRight(secondState)));
            Assert.Equal(secondState, _world.StateAt(second.X, second.Y + 1, second.Z));

            // Each is the other's partner, on both halves
            Assert.True(PairedAt(first, out var fromFirst));
            Assert.Equal(new Vector3(second.X, second.Y, second.Z), fromFirst);
            Assert.True(PairedAt(second, out var fromSecond));
            Assert.Equal(new Vector3(first.X, first.Y, first.Z), fromSecond);
            Assert.True(PairedAt((first.X, first.Y + 1, first.Z), out var upper));
            Assert.Equal(new Vector3(second.X, second.Y + 1, second.Z), upper);
        }
    }

    [Fact]
    public void ADoorThatWouldNotMakeAProperDoubleDoorStaysPlain()
    {
        _world = new SupportTestWorld();
        var door = Door();
        _world.Load(8, _world.Top, 8);
        var first = PlaceDoor(door.Id, 4, 8, 0);
        var latch = DoubleDoor.LatchSide(_world.StateAt(first.X, first.Y, first.Z));
        var column = new Vector3(4, 0, 8);

        // Beside the first door's hinge: two doors hinged the same way, no pair
        var hingeSide = PlaceDoorAt(door.Id, column - latch, 0);
        Assert.False(DoorState.HingeRight(_world.StateAt(hingeSide.X, hingeSide.Y, hingeSide.Z)));
        Assert.False(PairedAt(hingeSide, out _));
        Assert.False(PairedAt(first, out _));

        // On the free edge but facing the other way: its panel stands on the far side of the cell
        Assert.True(_world.Break(hingeSide.X, hingeSide.Y, hingeSide.Z));
        var turned = PlaceDoorAt(door.Id, column + latch, 1);
        Assert.False(DoorState.HingeRight(_world.StateAt(turned.X, turned.Y, turned.Z)));
        Assert.False(PairedAt(first, out _));

        // Beside a door that already has its partner: the third door stands alone
        Assert.True(_world.Break(turned.X, turned.Y, turned.Z));
        var second = PlaceDoorAt(door.Id, column + latch, 0);
        Assert.True(PairedAt(second, out _));
        var third = PlaceDoorAt(door.Id, column + 2 * latch, 0);
        Assert.False(DoorState.HingeRight(_world.StateAt(third.X, third.Y, third.Z)));
        Assert.False(PairedAt(third, out _));
        Assert.True(PairedAt(second, out var partner));
        Assert.Equal(new Vector3(first.X, first.Y, first.Z), partner);
    }

    [Fact]
    public void AClickOnAnyCellOfADoubleDoorSwingsBothDoorsAndALoneDoorSwingsAlone()
    {
        _world = new SupportTestWorld();
        var door = Door();
        _world.Load(8, _world.Top, 8);
        var first = PlaceDoor(door.Id, 4, 8, 0);
        var firstState = _world.StateAt(first.X, first.Y, first.Z);
        var second = PlaceDoorAt(door.Id, new Vector3(4, 0, 8) + DoubleDoor.LatchSide(firstState), 0);
        var secondState = _world.StateAt(second.X, second.Y, second.Z);
        (int X, int Y, int Z)[] cells =
            [first, (first.X, first.Y + 1, first.Z), second, (second.X, second.Y + 1, second.Z)];

        var open = false;
        foreach (var clicked in cells)
        {
            ClickBoxes(clicked.X, clicked.Y, clicked.Z);
            open = !open;
            // Every cell takes the new open bit and keeps its own facing and hinge
            foreach (var cell in cells)
            {
                var own = cell.X == first.X ? firstState : secondState;
                Assert.Equal(DoorState.WithOpen(own, open), _world.StateAt(cell.X, cell.Y, cell.Z));
            }
        }

        // A door set beside an open single door joins it open
        Assert.True(_world.Break(second.X, second.Y, second.Z));
        ClickBoxes(first.X, first.Y, first.Z);
        var firstOpen = DoorState.IsOpen(_world.StateAt(first.X, first.Y, first.Z));
        var rejoined = PlaceDoorAt(door.Id, new Vector3(second.X, 0, second.Z), 0);
        Assert.Equal(firstOpen, DoorState.IsOpen(_world.StateAt(rejoined.X, rejoined.Y, rejoined.Z)));

        // With its partner broken the mirrored door is a single door again and swings alone
        Assert.True(_world.Break(first.X, first.Y + 1, first.Z));
        Assert.False(PairedAt(rejoined, out _));
        ClickBoxes(rejoined.X, rejoined.Y, rejoined.Z);
        var alone = _world.StateAt(rejoined.X, rejoined.Y, rejoined.Z);
        Assert.Equal((!firstOpen, true), (DoorState.IsOpen(alone), DoorState.HingeRight(alone)));
        Assert.Equal(alone, _world.StateAt(rejoined.X, rejoined.Y + 1, rejoined.Z));
    }

    [Fact]
    public void ARightHingedDoorIsTheLeftHingedOneMirroredAcrossItsCell()
    {
        var hinged = BlockType.Types.Values.Where(t => t.SideHinged).ToArray();
        Assert.NotEmpty(hinged);
        foreach (var type in hinged)
        foreach (var open in new[] { false, true })
        {
            var left = Bounds(type.RotatedHitboxes![BlockType.ShapeIndex(DoorState.Encode(0, open), type.RotatedHitboxes.Length)]);
            var right = Bounds(type.RotatedHitboxes[BlockType.ShapeIndex(DoorState.Encode(0, open, hingeRight: true), type.RotatedHitboxes.Length)]);
            Assert.Equal(left.Min with { X = 1f - left.Max.X }, right.Min);
            Assert.Equal(left.Max with { X = 1f - left.Min.X }, right.Max);

            // The drawn model stays inside the same box, quad for quad wound outward
            var model = type.RotatedModels![BlockType.ShapeIndex(DoorState.Encode(0, open, hingeRight: true), type.RotatedModels.Length)];
            foreach (var quad in model.Quads)
            {
                var winding = Vector3.Cross(quad.Positions[1] - quad.Positions[0], quad.Positions[2] - quad.Positions[0]);
                Assert.True(Vector3.Dot(winding, quad.Normal) > 0f);
                Assert.All(quad.Positions, p => Assert.InRange(p.X, right.Min.X - 1e-4f, right.Max.X + 1e-4f));
            }
        }
    }

    private static (Vector3 Min, Vector3 Max) Bounds(BlockHitbox[] boxes) =>
        (boxes.Select(b => b.Min).Aggregate(Vector3.Min), boxes.Select(b => b.Max).Aggregate(Vector3.Max));
}
