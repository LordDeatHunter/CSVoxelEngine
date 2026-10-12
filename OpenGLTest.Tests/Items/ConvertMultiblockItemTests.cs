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
    private static byte MudBricks => ContentRegistry.RequireBlock("mud_bricks").Id;
    private static byte Kiln => ContentRegistry.RequireBlock("mud_brick_kiln").Id;

    [Fact]
    public void ItemIsBuiltFromItsActionAndResolvesTheKiln()
    {
        var converter = Converter;
        Assert.IsType<ConvertMultiblockItem>(converter);
        Assert.Equal(ItemUseAction.ConvertMultiblock, converter.UseAction);
        Assert.Equal(MudBricks, converter.UseBlockId);
        Assert.Equal(Kiln, converter.UseResultId);

        // The base rule would call this a tool because its result id is non-zero; the
        // converter is an endless stackable misc item instead.
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
    public void OnUseForwardsTheHitAndPunchesOnlyOnSuccess()
    {
        var converter = Converter;
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
        var call = Assert.Single(world.ConvertCalls);
        Assert.Equal(new Vector3(5, 6, 7), call.Cell);
        Assert.Equal(Vector3.UnitZ, call.Normal);
        Assert.Equal(MudBricks, call.SourceId);
        Assert.Equal(Kiln, call.AnchorId);

        // Endless: no stack spent, no durability taken, whatever the outcome
        Assert.Equal(0, world.Consumed);
        Assert.Equal(0, world.DamageCalls);

        world.ConvertResult = false;
        Assert.Equal(ItemUseResult.Ignored, converter.OnUse(new ItemUseContext(world, 5f, 0), null));
        Assert.Equal(2, world.ConvertCalls.Count);

        world.RaycastHits = false;
        Assert.Equal(ItemUseResult.Ignored, converter.OnUse(new ItemUseContext(world, 5f, 0), null));
        Assert.Equal(2, world.ConvertCalls.Count);
        Assert.Equal(0, world.Consumed);
        Assert.Equal(0, world.DamageCalls);
    }

    [Fact]
    public void EveryConverterTargetsAMultiblockBuiltFromItsSource()
    {
        var converters = ItemRegistry.All
            .Where(item => item.UseAction == ItemUseAction.ConvertMultiblock)
            .ToArray();
        Assert.NotEmpty(converters);

        foreach (var converter in converters)
        {
            var anchor = BlockType.ById[converter.UseResultId];
            Assert.NotNull(anchor);
            Assert.Equal(BlockRenderer.Multiblock, anchor!.Renderer);
            Assert.Equal(converter.UseBlockId, anchor.MultiblockSourceId);
        }
    }
}
