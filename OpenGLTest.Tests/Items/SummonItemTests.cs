using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Entities;
using VoxelEngine.Items;
using Xunit;

namespace VoxelEngine.Tests.Items;

/// <summary>
/// The summon item (<see cref="ItemUseAction.Summon"/> / <see cref="SummonItem"/>), here the dragonfly wing: using it on a cell the world
/// accepts wakes the boss it names and spends one unit; a cell the world refuses, or aiming at nothing, spends
/// nothing; creative spends none; the content ties the item to a real entity.
/// </summary>
public sealed class SummonItemTests
{
    public SummonItemTests() => TestContent.EnsureLoaded();

    private static Item Wing => ContentRegistry.RequireItem("dragonfly_wing");

    private static StubItemUseWorld AimedAt(int x, int y, int z) => new()
    {
        RaycastHits = true,
        RaycastHit = new Vector3(x + 0.4f, y + 0.9f, z + 0.1f),
        RaycastNormal = Vector3.UnitY,
    };

    [Fact]
    public void UsingAnAcceptedCellWakesTheBossAndSpendsOneUnit()
    {
        var world = AimedAt(4, 5, 6);
        world.Summonable.Add((4, 5, 6));

        var result = Wing.OnUse(new ItemUseContext(world, 4f, 0), null);

        Assert.True(result.Applied);
        Assert.Equal(1, world.Consumed);
        Assert.Equal(("sandworm", 4, 5, 6), Assert.Single(world.SummonCalls));
    }

    [Fact]
    public void AReusedCellTheWorldRefusesSpendsNothing()
    {
        var world = AimedAt(4, 5, 6);

        var result = Wing.OnUse(new ItemUseContext(world, 4f, 0), null);

        Assert.False(result.Applied);
        Assert.Equal(0, world.Consumed);
    }

    [Fact]
    public void AimingAtNothingDoesNothing()
    {
        var world = new StubItemUseWorld { RaycastHits = false };

        Assert.False(Wing.OnUse(new ItemUseContext(world, 4f, 0), null).Applied);
        Assert.Empty(world.SummonCalls);
        Assert.Equal(0, world.Consumed);
    }

    [Fact]
    public void CreativeSummonsWithoutSpendingTheItem()
    {
        var world = AimedAt(1, 2, 3);
        world.Summonable.Add((1, 2, 3));
        world.IsCreative = true;

        Assert.True(Wing.OnUse(new ItemUseContext(world, 4f, 0), null).Applied);
        Assert.Equal(0, world.Consumed);
    }

    [Fact]
    public void TheWingNamesARealEntity()
    {
        var item = (SummonItem)Wing;
        Assert.Equal(ItemUseAction.Summon, item.UseAction);
        Assert.NotNull(EntityRegistry.TryGet(item.UseEntityName!));
    }
}
