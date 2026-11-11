using VoxelEngine.Items;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// The battle tower's lock blocks: the stairwell gate, the skeleton pile and the roof altar are worldgen-only and
/// unbreakable, so only the tower itself removes them, and nothing (no item, recipe or loot table) hands one out; the gate is a solid, see-through cell that passes light, and the pile is walked
/// through but never taken over by a flow or a placement.
/// </summary>
public sealed class TowerLockBlockTests
{
    public TowerLockBlockTests() => TestContent.EnsureLoaded();

    private static BlockType Door => ContentRegistry.RequireBlock("tower_door");
    private static BlockType Pile => ContentRegistry.RequireBlock("skeleton_pile");
    private static BlockType Altar => ContentRegistry.RequireBlock("tower_altar");

    [Fact]
    public void TheLockBlocksAreUnbreakableWorldgenOnlyBlocks()
    {
        foreach (var type in new[] { Door, Pile, Altar })
        {
            Assert.True(BlockType.IsUnbreakable(type.Id), type.Name);
            Assert.False(type.HasItem, type.Name);
            Assert.Null(ItemRegistry.TryGet(type.Id));
        }
    }

    [Fact]
    public void NoRecipeOrLootTableHandsOutALockBlock()
    {
        foreach (var type in new[] { Door, Pile, Altar })
        {
            Assert.DoesNotContain(ContentRegistry.Recipes, r => r.ResultId == type.Id);
            foreach (var table in ContentRegistry.LootTables.Values)
                Assert.DoesNotContain(table.AllEntries(), e => !e.IsEmpty && e.Nested == null && e.Item == type.Id);
        }
    }

    [Fact]
    public void TheDoorIsASolidSeeThroughGateThatPassesLight()
    {
        Assert.True(BlockType.IsSolid(Door.Id));
        Assert.False(BlockType.Occludes(Door.Id));
        Assert.True(BlockType.PassesLightById[Door.Id]);
    }

    [Fact]
    public void APileIsWalkedThroughButNeverWashedOrPlacedOver()
    {
        Assert.False(BlockType.IsSolid(Pile.Id));
        Assert.False(BlockType.IsReplaceable(Pile.Id));
        Assert.False(BlockType.IsReplaceableByPlacement(Pile.Id, 0));
    }
}
