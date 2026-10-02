using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Tree wood drops by thickness: <see cref="DropManager.RollLoot"/> reads the cell's
/// <see cref="WoodState"/> from the loot context and rolls exactly the band
/// <see cref="WoodLootBand.Select"/> picks for it. Loot rolls are random, so the comparisons are
/// over the set of ids many rolls produce rather than one roll's output.
/// </summary>
public sealed class WoodLootTests
{
    public WoodLootTests() => TestContent.EnsureLoaded();

    // Enough rolls that even a rare entry shows up (a 15 % pool misses all of them with
    // probability far below 1e-12)
    private const int Rolls = 400;

    private static IEnumerable<BlockType> TreeWoods =>
        BlockType.Types.Values.Where(t => BlockType.IsTreeWood(t.Id));

    private static HashSet<byte> Support(System.Action<List<byte>> roll)
    {
        var support = new HashSet<byte>();
        var into = new List<byte>();
        for (var i = 0; i < Rolls; i++)
        {
            into.Clear();
            roll(into);
            support.UnionWith(into);
        }
        return support;
    }

    [Fact]
    public void DetachedWoodDropsWhatItsTopBandSays()
    {
        Assert.NotEmpty(TreeWoods);
        foreach (var wood in TreeWoods)
        {
            var top = wood.WoodLootBands[^1];
            var context = new LootContext(3, 40, -7, State: WoodState.Detached);
            var rolled = Support(into => DropManager.RollLoot(wood.Id, context, into));
            if (top.Self)
                Assert.Equal([wood.Id], rolled);
            else
                Assert.Equal(Support(into => top.Table?.Roll(context, into)), rolled);
        }
    }

    [Fact]
    public void EveryThicknessRollsTheBandSelectPicks()
    {
        foreach (var wood in TreeWoods)
        {
            for (var t = 0; t <= WoodState.MaxThickness; t++)
            {
                var state = WoodState.Encode(t, WoodState.ParentRoot, false);
                var context = new LootContext(3, 40, -7, State: state);
                var band = WoodLootBand.Select(wood.WoodLootBands, t);

                var rolled = Support(into => DropManager.RollLoot(wood.Id, context, into));
                var expected = band.Self
                    ? [wood.Id]
                    : Support(into => band.Table?.Roll(context, into));
                Assert.True(expected.SetEquals(rolled),
                    $"{wood.Name} at thickness {t} rolled [{string.Join(", ", rolled)}], its band [{string.Join(", ", expected)}]");
            }
        }
    }
}
