using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The acorn (<see cref="ItemUseAction.PlantTree"/> / <see cref="PlantTreeItem"/>): using one on
/// ground plants an oak sapling there and spends it; anything that is not ground refuses it.
/// </summary>
public sealed class AcornTests
{
    public AcornTests() => TestContent.EnsureLoaded();

    [Fact]
    public void AnAcornPlantsASaplingOnTheAimedGround()
    {
        var world = new StubItemUseWorld
        {
            RaycastHits = true,
            RaycastHit = new Vector3(4, 5, 6),
            RaycastNormal = Vector3.UnitY,
        };
        world.Plantable["oak"] = [(4, 5, 6)];
        var acorn = ContentRegistry.RequireItem("acorn");
        var result = acorn.OnUse(new ItemUseContext(world, 4f, 0), null);

        Assert.True(result.Applied);
        Assert.Equal(1, world.Consumed);
        Assert.Equal(("oak", 4, 5, 6), Assert.Single(world.PlantTreeCalls));
    }

    [Fact]
    public void AnAcornOnBadGroundIsRefusedAndSpendsNothing()
    {
        var world = new StubItemUseWorld
        {
            RaycastHits = true,
            RaycastHit = new Vector3(4, 5, 6),
            RaycastNormal = Vector3.UnitY,
        };
        var acorn = ContentRegistry.RequireItem("acorn");
        Assert.False(acorn.OnUse(new ItemUseContext(world, 4f, 0), null).Applied);
        Assert.Equal(0, world.Consumed);

        // A hit on a side face plants on the cell across it
        world.Plantable["oak"] = [(5, 5, 6)];
        world.RaycastNormal = Vector3.UnitX;
        Assert.True(acorn.OnUse(new ItemUseContext(world, 4f, 0), null).Applied);
        Assert.Equal((5, 5, 6), (world.PlantTreeCalls[1].X, world.PlantTreeCalls[1].Y, world.PlantTreeCalls[1].Z));
    }

    [Fact]
    public void OakLeavesCarryTheAcornAndOtherLeavesDoNot()
    {
        var oak = ContentRegistry.RequireBlock("oak_leaves");
        var spruce = ContentRegistry.RequireBlock("spruce_leaves");
        var acorn = ContentRegistry.RequireItem("acorn").Id;
        Assert.NotNull(oak.LootTable);
        Assert.NotSame(oak.LootTable, spruce.LootTable);

        var into = new System.Collections.Generic.List<byte>();
        var acorns = 0;
        for (var i = 0; i < 3000; i++)
        {
            into.Clear();
            oak.LootTable!.Roll(new LootContext(0, 0, 0), into);
            if (into.Contains(acorn)) acorns++;
        }
        Assert.InRange(acorns / 3000f, 0.01f, 0.12f);
    }
}
