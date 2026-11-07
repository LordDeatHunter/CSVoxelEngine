using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Spells;
using VoxelEngine.Items;
using Xunit;

namespace VoxelEngine.Tests.Items;

/// <summary>
/// A teaching scroll (<see cref="ItemUseAction.LearnParts"/> / <see cref="LearnPartsItem"/>): it teaches its
/// parts and is spent once, is refused and kept when it teaches nothing new, and creative learns without
/// spending it.
/// </summary>
public sealed class LearnPartsItemTests
{
    public LearnPartsItemTests() => TestContent.EnsureLoaded();

    private static string[] TwoKeys() => SpellPartRegistry.All.Take(2).Select(p => p.Key).ToArray();

    internal static LearnPartsItem Scroll(params string[] teaches)
    {
        var spec = new ItemFactory.ItemSpec(7, "Scroll", "t", ItemUseAction.LearnParts,
            0, 0, 0, 1f, 0, 0f, 1, 1, 1, ItemAnimationMode.None, 8f,
            Vector3.Zero, Vector3.Zero, 1f, false, 0.5f, 300f, AttackPattern.Punch, 0.5f, 1f, null, "scroll",
            Teaches: teaches);
        return Assert.IsType<LearnPartsItem>(ItemFactory.Create("scroll", spec));
    }

    [Fact]
    public void AScrollTeachesItsPartsAndIsSpentOnce()
    {
        var keys = TwoKeys();
        var world = new StubItemUseWorld();

        var result = Scroll(keys).OnUse(new ItemUseContext(world, 4f, 0), null);

        Assert.True(result.Applied);
        Assert.Equal(1, world.Consumed);
        Assert.All(keys, k => Assert.True(world.Inventory.KnowsPart(k)));
    }

    [Fact]
    public void AScrollThatTeachesNothingNewIsRefusedAndKept()
    {
        var scroll = Scroll(TwoKeys());
        var world = new StubItemUseWorld();
        scroll.OnUse(new ItemUseContext(world, 4f, 0), null);

        var again = scroll.OnUse(new ItemUseContext(world, 4f, 0), null);

        Assert.False(again.Applied);
        Assert.Equal(1, world.Consumed);
    }

    [Fact]
    public void CreativeLearnsWithoutSpendingTheScroll()
    {
        var keys = TwoKeys();
        var world = new StubItemUseWorld { IsCreative = true };

        var result = Scroll(keys).OnUse(new ItemUseContext(world, 4f, 0), null);

        Assert.True(result.Applied);
        Assert.Equal(0, world.Consumed);
        Assert.All(keys, k => Assert.True(world.Inventory.KnowsPart(k)));
    }
}
