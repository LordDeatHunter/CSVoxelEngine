using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class ContentTests
{
    public ContentTests() => TestContent.EnsureLoaded();

    [Fact]
    public void BlockAndItemIdsFitInSharedByteRange()
    {
        var ids = BlockType.Types.Keys.Concat(ItemType.Types.Keys).ToArray();
        Assert.NotEmpty(ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id => Assert.NotEqual(BlockType.Air, id));
        Assert.True(ids.Length <= 255);
    }

    [Fact]
    public void WaterIsForcedToFluidBehaviour()
    {
        var water = ContentRegistry.RequireBlock("water");
        Assert.False(water.Solid);
        Assert.True(water.Fluid);
        Assert.True(water.PassesLight);
        Assert.False(water.HasItem);
        Assert.Same(LootTable.None, water.LootTable);
        Assert.True(BlockType.IsFluid(water.Id));
    }

    [Fact]
    public void FullCubeOccludesAfterBake()
    {
        Assert.True(ContentRegistry.RequireBlock("grass").OccludesNeighbours);
    }

    [Fact]
    public void StoneAlwaysDropsOneCobblestone()
    {
        var table = ContentRegistry.LootTableFor("stone");
        Assert.NotNull(table);
        var cobble = ContentRegistry.RequireBlock("cobblestone").Id;
        var into = new List<byte>();
        for (var i = 0; i < 10; i++)
        {
            into.Clear();
            table.Roll(new LootContext(0, 0, 0), into);
            Assert.Equal(new[] { cobble }, into);
        }
    }

    [Fact]
    public void NoneLootTableDropsNothing()
    {
        var into = new List<byte>();
        LootTable.None.Roll(new LootContext(0, 0, 0), into);
        Assert.Empty(into);
    }

    [Fact]
    public void UnknownContentThrowsNamingIt()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ContentRegistry.RequireBlock("nope"));
        Assert.Contains("nope", ex.Message);
    }
}
