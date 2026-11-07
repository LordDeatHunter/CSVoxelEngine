using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Entities;
using VoxelEngine.Items;
using Xunit;

namespace VoxelEngine.Tests.Items;

/// <summary>
/// The thumper (<see cref="ItemUseAction.Summon"/> / <see cref="SummonItem"/>): using it on a cell the world
/// accepts wakes the boss it names and spends one unit; a cell the world refuses, or aiming at nothing, spends
/// nothing; creative spends none; the content ties the item to a real entity and a way to craft it.
/// </summary>
public sealed class SummonItemTests
{
    public SummonItemTests() => TestContent.EnsureLoaded();

    private static Item Thumper => ContentRegistry.RequireItem("thumper");

    private static StubItemUseWorld AimedAt(int x, int y, int z) => new()
    {
        RaycastHits = true,
        RaycastHit = new Vector3(x + 0.4f, y + 0.9f, z + 0.1f),
        RaycastNormal = Vector3.UnitY,
    };

    [Fact]
    public void ThumpingAnAcceptedCellWakesTheBossAndSpendsOneUnit()
    {
        var world = AimedAt(4, 5, 6);
        world.Summonable.Add((4, 5, 6));

        var result = Thumper.OnUse(new ItemUseContext(world, 4f, 0), null);

        Assert.True(result.Applied);
        Assert.Equal(1, world.Consumed);
        Assert.Equal(("sandworm", 4, 5, 6), Assert.Single(world.SummonCalls));
    }

    [Fact]
    public void AReusedCellTheWorldRefusesSpendsNothing()
    {
        var world = AimedAt(4, 5, 6);

        var result = Thumper.OnUse(new ItemUseContext(world, 4f, 0), null);

        Assert.False(result.Applied);
        Assert.Equal(0, world.Consumed);
    }

    [Fact]
    public void AimingAtNothingDoesNothing()
    {
        var world = new StubItemUseWorld { RaycastHits = false };

        Assert.False(Thumper.OnUse(new ItemUseContext(world, 4f, 0), null).Applied);
        Assert.Empty(world.SummonCalls);
        Assert.Equal(0, world.Consumed);
    }

    [Fact]
    public void CreativeSummonsWithoutSpendingTheItem()
    {
        var world = AimedAt(1, 2, 3);
        world.Summonable.Add((1, 2, 3));
        world.IsCreative = true;

        Assert.True(Thumper.OnUse(new ItemUseContext(world, 4f, 0), null).Applied);
        Assert.Equal(0, world.Consumed);
    }

    [Fact]
    public void TheThumperNamesARealEntityAndCanBeCrafted()
    {
        var item = (SummonItem)Thumper;
        Assert.Equal(ItemUseAction.Summon, item.UseAction);
        Assert.NotNull(EntityRegistry.TryGet(item.UseEntityName!));
        Assert.NotNull(ContentRegistry.RecipeByName("thumper"));
    }
}
