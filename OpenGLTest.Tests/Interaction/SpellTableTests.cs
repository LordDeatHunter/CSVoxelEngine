using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Interaction;
using VoxelEngine.Tests.Magic;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// The spell table (a <see cref="BlockType.SpellBuilder"/> block): a right-click claims the click as
/// <see cref="RightClickOutcome.SpellTable"/> ahead of a held staff's cast, with no side effect, while a
/// block in hand still places against it like against a kiln.
/// </summary>
[Collection("Worldgen seed")]
public sealed class SpellTableTests : IDisposable
{
    private SupportTestWorld? _world;

    public SpellTableTests()
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

    private static ushort Table() => BlockType.Types.Values.First(t => t.SpellBuilder).Id;

    // A table two cells north of the player on the plateau; returns its cell
    private Vector3 StandTable(out int x, out int y, out int z)
    {
        _world = new SupportTestWorld();
        (x, y, z) = (8, _world.Top + 1, 6);
        _world.Load(x, y, z);
        _world.Set(x, y, z, Table());
        return new Vector3(x, y, z);
    }

    [Fact]
    public void AStaffClickOnTheTableIsClaimedAsTheTableNotACast()
    {
        var cell = StandTable(out var x, out var y, out var z);
        StaffFixture.Hold(_world!.Player.Inventory, "wooden_staff", out var staff, out var stack);
        StaffFixture.Bind(staff, stack, 0, out _);
        var before = stack.Get<StaffComponent>();

        var eye = _world.Player.Context.Eye;
        _world.Player.Aim(eye, Vector3.Normalize(cell + new Vector3(0.5f) - eye));
        Assert.True(_world.World.TryUseSelected(World.InteractionReach, _world.Player.Context, out var outcome));

        Assert.Equal(RightClickOutcome.SpellTable, outcome);
        Assert.Equal(Table(), _world.IdAt(x, y, z));
        Assert.Equal(before, stack.Get<StaffComponent>());
    }

    [Fact]
    public void ABlockInHandPlacesAgainstTheTable()
    {
        var cell = StandTable(out var x, out var y, out var z);
        var planks = ContentRegistry.RequireBlock("oak_planks").Id;
        _world!.Player.Inventory.Add(planks);
        Assert.True(_world.Player.Inventory.PickBlock(planks));

        // Aim at the table's top face: the plank lands on top of it
        var eye = _world.Player.Context.Eye;
        _world.Player.Aim(eye, Vector3.Normalize(cell + new Vector3(0.5f, 0.99f, 0.5f) - eye));
        Assert.True(_world.World.TryUseSelected(World.InteractionReach, _world.Player.Context, out var outcome));

        Assert.Equal(RightClickOutcome.Place, outcome);
        Assert.Equal(Table(), _world.IdAt(x, y, z));
        Assert.Equal(planks, _world.IdAt(x, y + 1, z));
    }

    [Fact]
    public void EverySpellBuilderBlockIsCraftable()
    {
        var tables = BlockType.Types.Values.Where(t => t.SpellBuilder).ToArray();
        Assert.NotEmpty(tables);
        foreach (var table in tables)
        {
            Assert.True(table.HasItem, $"{table.Name} has no item");
            Assert.Contains(ContentRegistry.Recipes, r => r.ResultId == table.Id);
        }
    }
}
