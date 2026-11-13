using System.Linq;
using System.Numerics;
using VoxelEngine.Items;
using VoxelEngine.Simulation.Ingots;
using Xunit;

namespace VoxelEngine.Tests.Items;

/// <summary>
/// Ingot piles (<see cref="PlaceIngotItem"/>, <see cref="IngotPileManager"/>): every ingot item lays onto the same
/// pile in the order it was placed, a full pile takes no more, and the layout alternates direction per layer.
/// </summary>
public sealed class IngotPileTests
{
    public IngotPileTests() => TestContent.EnsureLoaded();

    private static ushort Pile => ContentRegistry.RequireBlock("ingot_pile").Id;

    private static StubItemUseWorld PileWorld()
    {
        var world = new StubItemUseWorld { RaycastHits = true, RaycastHit = new Vector3(2, 3, 4), RaycastNormal = Vector3.UnitY };
        world.Cells[(2, 3, 4)] = (Pile, 0);
        return world;
    }

    private static Item Ingot(string name) => ContentRegistry.RequireItem(name);

    [Fact]
    public void MixedMetalsStackInPlacingOrderAndEachSpendsOneUnit()
    {
        var world = PileWorld();
        var copper = Ingot("copper_ingot");
        var tin = Ingot("tin_ingot");
        Assert.True(copper.OnUse(new ItemUseContext(world, 5f, 0), null).Applied);
        Assert.True(tin.OnUse(new ItemUseContext(world, 5f, 0), null).Applied);
        Assert.Equal([copper.Id, tin.Id], world.Ingots[(2, 3, 4)]);
        Assert.Equal(2, world.Consumed);
    }

    [Fact]
    public void AFullPileTakesNoMoreIngots()
    {
        var world = PileWorld();
        var copper = Ingot("copper_ingot");
        for (var i = 0; i < IngotPileConfig.Capacity; i++)
            Assert.True(copper.OnUse(new ItemUseContext(world, 5f, 0), null).Applied);
        var consumed = world.Consumed;
        Assert.False(copper.OnUse(new ItemUseContext(world, 5f, 0), null).Applied);
        Assert.Equal(IngotPileConfig.Capacity, world.Ingots[(2, 3, 4)].Count);
        Assert.Equal(consumed, world.Consumed);
    }

    [Fact]
    public void EveryIngotItemNamesACubeToDrawAndTheFullPileFitsItsCell()
    {
        var ingots = new[] { "copper_ingot", "tin_ingot", "bronze_ingot", "wrought_iron", "refined_vulcanite" }.Select(Ingot).ToArray();
        Assert.All(ingots, item => Assert.True(IngotPileManager.IsIngot(item.Id) && item.IngotBlockId != 0));
        var pile = BlockType.ById[Pile]!;
        Assert.True(IngotPileConfig.FullHeight <= pile.Hitboxes.Max(h => h.Max.Y) + 1e-4f);
        Assert.True(IngotPileConfig.PerLayer * IngotPileConfig.IngotWidth + (IngotPileConfig.PerLayer - 1) * IngotPileConfig.Gap <= 1f);
        Assert.True(IngotPileConfig.IngotLength <= 1f);
    }
}
