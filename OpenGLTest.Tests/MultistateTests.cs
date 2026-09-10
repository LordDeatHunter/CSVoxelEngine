using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Multi-state blocks (candle-style piles): flint grounds and grows pebble piles of 1-3,
/// with the count riding the chunk state byte. Worldgen piles never write state (0 keeps the
/// position-hash default); player piles store 1..max explicitly.
/// </summary>
public sealed class MultistateTests
{
    public MultistateTests() => TestContent.EnsureLoaded();

    private static byte Pebble => ContentRegistry.RequireBlock("pebble").Id;
    private static byte Flint => ContentRegistry.RequireItem("flint").Id;

    [Fact]
    public void PebbleIsAThreeStateStackableBlock()
    {
        Assert.Equal(3, BlockType.ById[Pebble]!.StackMax);
    }

    [Fact]
    public void FlintSpendsFromItsStackToBuildPebbles()
    {
        var flint = ItemType.Types[Flint];
        Assert.Equal(ItemUseAction.PlaceStack, flint.UseAction);
        Assert.Equal(Pebble, flint.UseBlockId);
        Assert.Equal(0, flint.UseResultId);
    }

    [Fact]
    public void FlintStaysAMiscPickupWhileBucketsStayTools()
    {
        Assert.Equal(InventoryCategory.Misc, Inventory.CategoryOf(Flint));
        Assert.Equal(InventoryCategory.Tools,
            Inventory.CategoryOf(ContentRegistry.RequireItem("bucket").Id));
    }

    [Fact]
    public void ExplicitStateWinsOverTheWorldgenHash()
    {
        var pebble = BlockType.ById[Pebble]!;
        for (var x = 0; x < 8; x++)
        for (var z = 0; z < 8; z++)
        {
            Assert.Equal(1, PebbleGenerator.CountWithState(x, z, 1, pebble.StackMax));
            Assert.Equal(2, PebbleGenerator.CountWithState(x, z, 2, pebble.StackMax));
            Assert.Equal(3, PebbleGenerator.CountWithState(x, z, 3, pebble.StackMax));
            Assert.Equal(PebbleGenerator.CountAt(x, z),
                PebbleGenerator.CountWithState(x, z, 0, pebble.StackMax));
        }
    }

    [Fact]
    public void PebbleLootDropsTheExplicitPile()
    {
        var table = ContentRegistry.LootTableFor("pebble");
        Assert.NotNull(table);

        var into = new List<byte>();
        table.Roll(new LootContext(8, 0, 8, Count: 2), into);
        Assert.Equal([Flint, Flint], into);

        into.Clear();
        table.Roll(new LootContext(8, 0, 8), into);
        Assert.Equal(PebbleGenerator.CountAt(8, 8), into.Count);
        Assert.All(into, id => Assert.Equal(Flint, id));
    }

    [Fact]
    public void PileCountPassesOnlyExplicitStackStatesThrough()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        Assert.Equal(0, DropManager.PileCount(Pebble, 8, 8, 0));
        Assert.Equal(2, DropManager.PileCount(Pebble, 8, 8, 2));
        Assert.Equal(0, DropManager.PileCount(Pebble, 8, 8, 9));
        Assert.Equal(0, DropManager.PileCount(stone, 8, 8, 2));
    }

    [Fact]
    public void ChunkStateRoundtripsThroughRawWrites()
    {
        var (_, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, Pebble, 2);
        Assert.Equal(2, center.GetLocalState(8, 8, 8));
        center.SetBlockRaw(8, 8, 8, Pebble, 0);
        Assert.Equal(0, center.GetLocalState(8, 8, 8));
    }

    [Fact]
    public void PebbleMeshGrowsOneStonePerState()
    {
        // One stone is five emitted faces (the bottom sits on the ground): 20 verts, 30 indices.
        foreach (var state in new byte[] { 1, 2, 3 })
        {
            var (manager, center) = MeshTestWorld.WalledArea();
            center.SetBlockRaw(8, 8, 8, Pebble, state);

            var mesh = center.GenerateMeshData(manager, greedy: false);
            Assert.NotNull(mesh);
            Assert.Equal(20 * state, mesh.Vertices.Count);
            Assert.Equal(30 * state, mesh.Indices.Count);
        }
    }

    [Fact]
    public void WorldgenPebbleMeshMatchesItsHashCount()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, Pebble, 0);

        var expected = PebbleGenerator.CountAt(8, 8);
        var mesh = center.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(mesh);
        Assert.Equal(20 * expected, mesh.Vertices.Count);
    }
}
