using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Items;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Items;

public sealed class ConvertMultiblockItemTests
{
    public ConvertMultiblockItemTests() => TestContent.EnsureLoaded();

    private static Item Converter => ContentRegistry.RequireItem("multiblock_converter");
    /// <summary>Every multiblock anchor in id order, the order the converter tries them in.</summary>
    private static BlockType[] Anchors => BlockType.ById
        .Where(type => type is { Renderer: BlockRenderer.Multiblock })
        .Select(type => type!)
        .ToArray();

    [Fact]
    public void ItemIsBuiltFromItsActionAndIsAnEndlessMiscItem()
    {
        var converter = Converter;
        Assert.IsType<ConvertMultiblockItem>(converter);
        Assert.Equal(ItemUseAction.ConvertMultiblock, converter.UseAction);

        // An endless stackable misc item, not a tool
        Assert.Equal(InventoryCategory.Misc, converter.StorageCategory);
        Assert.Equal(InventoryCategory.Misc, Inventory.CategoryOf(converter.Id));

        var recipe = ContentRegistry.RecipeByName("multiblock_converter");
        Assert.NotNull(recipe);

        var inventory = new Inventory();
        // The starting kit already seeds one converter (see Inventory's constructor), so
        // crafting a second brings the pool to two; missing ingredients still fail the
        // craft regardless of that seeded stock.
        var startingCount = inventory.CountFor(converter.Id);
        Assert.False(inventory.TryCraft(recipe!));
        Assert.True(inventory.Add(ContentRegistry.RequireItem("branch").Id));
        Assert.True(inventory.Add(ContentRegistry.RequireItem("mud_brick").Id, 2));
        Assert.True(inventory.TryCraft(recipe!));
        Assert.Equal(startingCount + 1, inventory.CountFor(converter.Id));
    }

    [Fact]
    public void OnUseTriesEveryStructureWithItsOwnSourceAndStopsAtTheFirstThatConverts()
    {
        var converter = Converter;
        var anchors = Anchors;
        Assert.True(anchors.Length > 1);
        var world = new StubItemUseWorld
        {
            RaycastHits = true,
            RaycastHit = new Vector3(5, 6, 7),
            RaycastNormal = Vector3.UnitZ,
            ConvertResult = true,
        };

        var result = converter.OnUse(new ItemUseContext(world, 5f, 0), null);
        Assert.True(result.Applied);
        Assert.True(result.Punch);
        Assert.Null(result.MorphToId);
        // The first structure tried converted, so no other was
        var call = Assert.Single(world.ConvertCalls);
        Assert.Equal(new Vector3(5, 6, 7), call.Cell);
        Assert.Equal(Vector3.UnitZ, call.Normal);
        Assert.Equal(anchors[0].Id, call.AnchorId);
        Assert.Equal(anchors[0].MultiblockSourceId, call.SourceId);

        // Endless: no stack spent, no durability taken, whatever the outcome
        Assert.Equal(0, world.Consumed);
        Assert.Equal(0, world.DamageCalls);

        // Nothing converts: every structure was tried once, each with the block it is built from
        world.ConvertCalls.Clear();
        world.ConvertResult = false;
        Assert.Equal(ItemUseResult.Ignored, converter.OnUse(new ItemUseContext(world, 5f, 0), null));
        Assert.Equal(anchors.Select(a => (a.MultiblockSourceId, a.Id)),
            world.ConvertCalls.Select(c => (c.SourceId, c.AnchorId)));

        world.RaycastHits = false;
        Assert.Equal(ItemUseResult.Ignored, converter.OnUse(new ItemUseContext(world, 5f, 0), null));
        Assert.Equal(anchors.Length, world.ConvertCalls.Count);
        Assert.Equal(0, world.Consumed);
        Assert.Equal(0, world.DamageCalls);
    }

    [Fact]
    public void EveryStructureIsBuiltFromABlockAPlayerCanHoldAndNoTwoShareOne()
    {
        // The converter finds a structure by the block it is built from, so that block must be
        // placeable and must name one structure only
        var sources = Anchors.Select(anchor => anchor.MultiblockSourceId).ToArray();
        Assert.Equal(sources.Length, sources.Distinct().Count());
        foreach (var source in sources)
            Assert.True(BlockType.ById[source]!.HasItem);
    }
}
