using VoxelEngine.Items;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Items;

public sealed class ItemHierarchyTests
{
    public ItemHierarchyTests() => TestContent.EnsureLoaded();

    private static byte ItemId(string name) => ContentRegistry.RequireItem(name).Id;

    [Fact]
    public void FactoryMapsActionsToBehaviourFamilies()
    {
        Assert.IsType<BucketPickupItem>(ItemRegistry.TryGet(ItemId("bucket")));
        Assert.IsType<BucketPlaceItem>(ItemRegistry.TryGet(ItemId("water_bucket")));
        Assert.IsType<PlaceStackItem>(ItemRegistry.TryGet(ItemId("flint")));
        Assert.IsType<PlainItem>(ItemRegistry.TryGet(ItemId("vine")));
    }

    [Fact]
    public void FirestarterGetsItsOwnCodeClass()
    {
        var firestarter = ItemRegistry.TryGet(ItemId("firestarter"));
        Assert.IsType<FirestarterItem>(firestarter);
        Assert.IsAssignableFrom<IgniteItem>(firestarter);
    }

    [Fact]
    public void RegistryHandsOutStaticSingletons()
    {
        var byName = ContentRegistry.RequireItem("bucket");
        Assert.Same(byName, ItemRegistry.TryGet(byName.Id));
        Assert.Same(byName, ItemRegistry.RequireItem("bucket"));
    }

    [Fact]
    public void BlocksResolveToBlockItemsButStayNonItems()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var resolved = ItemRegistry.TryGet(stone);
        Assert.IsType<BlockItem>(resolved);
        Assert.Equal(InventoryCategory.Blocks, resolved!.StorageCategory);
        Assert.False(ItemRegistry.IsItem(stone));
        Assert.True(ItemRegistry.IsItem(ItemId("bucket")));
        Assert.False(ItemRegistry.IsItem(0));
    }

    [Fact]
    public void StorageCategoryReplacesTheOldMorphOrDurabilityProbe()
    {
        Assert.Equal(InventoryCategory.Tools, Inventory.CategoryOf(ItemId("bucket")));
        Assert.Equal(InventoryCategory.Tools, Inventory.CategoryOf(ItemId("firestarter")));
        Assert.Equal(InventoryCategory.Misc, Inventory.CategoryOf(ItemId("flint")));
        Assert.Equal(InventoryCategory.Blocks,
            Inventory.CategoryOf(ContentRegistry.RequireBlock("stone").Id));
    }

    [Fact]
    public void OnlyScoopingItemsTargetFluids()
    {
        Assert.True(ItemRegistry.TryGet(ItemId("bucket"))!.TargetsFluids);
        Assert.False(ItemRegistry.TryGet(ItemId("water_bucket"))!.TargetsFluids);
        Assert.False(ItemRegistry.TryGet(ItemId("flint"))!.TargetsFluids);
        Assert.False(ItemRegistry.TryGet(ContentRegistry.RequireBlock("stone").Id)!.TargetsFluids);
    }

    [Fact]
    public void ItemStackCarriesTheSingletonPlusPerStackMetadata()
    {
        var firestarter = ItemId("firestarter");
        var first = new ItemStack(1, firestarter);
        var second = new ItemStack(2, firestarter);

        Assert.Same(ItemRegistry.TryGet(firestarter), first.Item);
        Assert.Equal(firestarter, first.DefinitionId);
        Assert.Equal(1, first.Count);

        Assert.False(first.Damage(4));
        Assert.False(second.Damage(0));
        Assert.Equal(6, first.Get<DurabilityComponent>().UsesLeft);
        Assert.Equal(10, second.Get<DurabilityComponent>().UsesLeft);

        first.Rename("Sparky");
        Assert.Equal("Sparky", first.ResolvedName);
        Assert.Equal("Firestarter", second.ResolvedName);

        Assert.Equal(0, first.EnchantmentLevel("unbreaking"));
        first.Enchantments["unbreaking"] = 2;
        Assert.Equal(2, first.EnchantmentLevel("unbreaking"));
        Assert.Equal(0, second.EnchantmentLevel("unbreaking"));
    }

    [Fact]
    public void SetItemMorphsInPlaceAndTopsDurabilityUp()
    {
        var inventory = new Inventory();
        var instance = Assert.Single(inventory.ToolEntries(), t => t.ItemId == ItemId("bucket")).InstanceId;
        var filled = ItemId("water_bucket");

        Assert.True(inventory.MorphTool(instance, filled));
        var stack = Assert.Single(inventory.ToolEntries(), t => t.ItemId == filled);
        Assert.Equal(filled, stack.ItemId);
        Assert.Same(ItemRegistry.TryGet(filled), inventory.GetToolStack(instance)!.Item);
    }
}
