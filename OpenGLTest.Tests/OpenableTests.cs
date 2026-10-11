using System;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Openable blocks (the trapdoor): the open bit sits beside the facing in the state byte
/// (<see cref="DoorState"/>) and picks one of eight pre-turned shapes, and a right-click flips it
/// in the world, whatever is in hand, swapping what the block is drawn and hit as.
/// </summary>
[Collection("Worldgen seed")]
public sealed class OpenableTests : IDisposable
{
    private SupportTestWorld? _world;

    public OpenableTests()
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

    private static BlockType[] Openable() => BlockType.Types.Values.Where(t => t.Openable).ToArray();

    private static (Vector3 Min, Vector3 Max) Bounds(BlockHitbox[] boxes) =>
        (boxes.Select(b => b.Min).Aggregate(Vector3.Min), boxes.Select(b => b.Max).Aggregate(Vector3.Max));

    [Fact]
    public void TheOpenBitFlipsAloneAndPicksTheOpenHalfOfTheShapes()
    {
        for (var facing = 0; facing < 4; facing++)
        foreach (var open in new[] { false, true })
        {
            var state = DoorState.Encode(facing, open);
            Assert.Equal((facing, open), (DoorState.Facing(state), DoorState.IsOpen(state)));
            var toggled = DoorState.Toggle(state);
            Assert.Equal((facing, !open), (DoorState.Facing(toggled), DoorState.IsOpen(toggled)));
            Assert.Equal(state, DoorState.Toggle(toggled));

            // Eight shapes: the facing, plus four when open; a four-shape block reads the facing only
            Assert.Equal(facing + (open ? 4 : 0), BlockType.ShapeIndex(state, DoorState.ShapeCount));
            Assert.Equal(facing, BlockType.ShapeIndex(state, 4));
        }
    }

    [Fact]
    public void AnOpenableBlockHasAClosedAndAnOpenShapeForEveryFacing()
    {
        var openable = Openable();
        Assert.NotEmpty(openable);
        foreach (var type in openable)
        {
            Assert.Equal(DoorState.ShapeCount, type.RotatedModels!.Length);
            Assert.Equal(DoorState.ShapeCount, type.RotatedHitboxes!.Length);
            for (var facing = 0; facing < 4; facing++)
            {
                var closed = Bounds(type.RotatedHitboxes[BlockType.ShapeIndex(DoorState.Encode(facing, false), 8)]);
                var open = Bounds(type.RotatedHitboxes[BlockType.ShapeIndex(DoorState.Encode(facing, true), 8)]);
                Assert.NotEqual(closed, open);
            }
        }
    }

    [Fact]
    public void TheRegistryRefusesAnOpenObjectItCannotDraw()
    {
        const string path = "data/blocks/x.json";
        var model = System.Text.Json.JsonDocument.Parse("\"block/cube_all\"").RootElement;
        var box = System.Text.Json.JsonDocument.Parse("""{ "min": [0, 0, 0], "max": [1, 1, 0.2] }""").RootElement;

        Assert.Null(ContentRegistry.ParseOpen(new BlockDefinition(), horizontalFacing: true, path));
        var boxes = ContentRegistry.ParseOpen(new BlockDefinition { Open = new() { Model = model, Hitbox = box } }, true, path);
        Assert.Single(boxes!);

        foreach (var (definition, facing) in new[]
                 {
                     (new BlockDefinition { Open = new() { Model = model, Hitbox = box } }, false),
                     (new BlockDefinition { Open = new() { Hitbox = box } }, true),
                     (new BlockDefinition { Open = new() { Model = model } }, true),
                 })
            Assert.Contains(path, Assert.Throws<InvalidOperationException>(() =>
                ContentRegistry.ParseOpen(definition, facing, path)).Message);
    }

    [Fact]
    public void ARightClickOpensAndClosesATrapdoorWhateverIsInHand()
    {
        _world = new SupportTestWorld();
        // A one-cell openable block (a door's halves are covered by DoorTests)
        var trapdoor = Openable().First(t => t.UpperHalfId == 0 && t.LowerHalfId == 0).Id;
        var planks = ContentRegistry.RequireBlock("oak_planks").Id;
        var ground = _world.Top;
        var (x, z) = (8, 6);
        _world.Load(x, ground, z);

        // Placed looking north, two cells north of the player
        _world.Player.Context.ViewDirection = -Vector3.UnitZ;
        Assert.True(_world.Place(trapdoor, new Vector3(x, ground, z), Vector3.UnitY));
        var cell = new Vector3(x, ground + 1, z);
        Assert.False(DoorState.IsOpen(_world.StateAt(x, ground + 1, z)));

        // A plank in hand: the click opens the trapdoor instead of placing the plank
        _world.Player.Inventory.Add(planks);
        Assert.True(_world.Player.Inventory.PickBlock(planks));
        var held = _world.Player.Inventory.CountFor(planks);
        var eye = _world.Player.Context.Eye;
        Click(Vector3.Normalize(cell + new Vector3(0.5f, 0.1f, 0.5f) - eye));
        var opened = _world.StateAt(x, ground + 1, z);
        Assert.True(DoorState.IsOpen(opened));
        Assert.Equal(0, DoorState.Facing(opened));
        Assert.Equal(held, _world.Player.Inventory.CountFor(planks));
        Assert.Equal(BlockType.Air, _world.IdAt(x, ground + 2, z));

        // Open, it is hit as its open boxes: click their middle to close it again
        var openBoxes = BlockShape.BoxesAt(trapdoor, _world.Manager, x, ground + 1, z);
        var (min, max) = Bounds(openBoxes);
        Assert.NotEqual(Bounds(BlockType.ById[trapdoor]!.Hitboxes), (min, max));
        Click(Vector3.Normalize(cell + (min + max) * 0.5f - eye));
        Assert.False(DoorState.IsOpen(_world.StateAt(x, ground + 1, z)));
        Assert.Equal(trapdoor, _world.IdAt(x, ground + 1, z));

        void Click(Vector3 aim)
        {
            _world.Player.Aim(eye, aim);
            Assert.True(_world.World.TryUseSelected(World.InteractionReach, _world.Player.Context, out var outcome));
            Assert.NotEqual(RightClickOutcome.Place, outcome);
        }
    }
}
