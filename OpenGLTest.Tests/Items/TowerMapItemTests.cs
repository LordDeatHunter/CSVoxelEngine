using VoxelEngine.Content.Blocks;
using VoxelEngine.Items;
using Xunit;

namespace VoxelEngine.Tests.Items;

/// <summary>
/// The tower map (<see cref="ItemUseAction.MarkTower"/> / <see cref="TowerMapItem"/>): a use the world accepts (a tower
/// was marked) spends one map, a refused one (no tower left to mark) spends nothing, and creative spends none.
/// </summary>
public sealed class TowerMapItemTests
{
    public TowerMapItemTests() => TestContent.EnsureLoaded();

    private static Item Map => ContentRegistry.RequireItem("tower_map");

    [Fact]
    public void AMapThatMarksATowerIsSpent()
    {
        var world = new StubItemUseWorld { TowerToMark = true };

        Assert.True(Map.OnUse(new ItemUseContext(world, 4f, 0), null).Applied);
        Assert.Equal(1, world.MarkTowerCalls);
        Assert.Equal(1, world.Consumed);
    }

    [Fact]
    public void AMapWithNoTowerLeftToMarkIsKept()
    {
        var world = new StubItemUseWorld { TowerToMark = false };

        Assert.False(Map.OnUse(new ItemUseContext(world, 4f, 0), null).Applied);
        Assert.Equal(0, world.Consumed);
    }

    [Fact]
    public void CreativeMarksWithoutSpending()
    {
        var world = new StubItemUseWorld { TowerToMark = true, IsCreative = true };

        Assert.True(Map.OnUse(new ItemUseContext(world, 4f, 0), null).Applied);
        Assert.Equal(0, world.Consumed);
    }
}
