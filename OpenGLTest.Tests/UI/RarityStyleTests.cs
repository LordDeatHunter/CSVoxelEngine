using System;
using System.Linq;
using VoxelEngine.Items;
using VoxelEngine.UI;
using Xunit;

namespace VoxelEngine.Tests.UI;

/// <summary>The rarity style table: Common stays plain, and a rarer item is never dressed plainer.</summary>
public sealed class RarityStyleTests
{
    private static readonly Rarity[] All = Enum.GetValues<Rarity>();

    [Fact]
    public void CommonStaysPlainAndEveryRarityAboveItWearsChrome()
    {
        var common = RarityStyle.Of(Rarity.Common);
        Assert.False(common.HasChrome);
        Assert.Equal(UiTheme.SlotBorderColor, common.AccentOr(UiTheme.SlotBorderColor));

        foreach (var rarity in All.Where(r => r != Rarity.Common))
        {
            var style = RarityStyle.Of(rarity);
            Assert.True(style.HasChrome, $"{rarity} wears chrome");
            Assert.Equal(style.Color, style.AccentOr(UiTheme.SlotBorderColor));
        }
    }

    [Fact]
    public void AHigherRarityIsNeverDressedPlainerThanTheOneBelow()
    {
        Func<RarityStyle, float>[] layers =
            [s => s.Opacity, s => s.Wash, s => s.Glow, s => s.Sheen, s => s.Sparkle, s => s.PulseDepth];
        for (var i = 1; i < All.Length; i++)
        {
            var below = RarityStyle.Of(All[i - 1]);
            var above = RarityStyle.Of(All[i]);
            foreach (var layer in layers)
                Assert.True(layer(above) >= layer(below), $"{All[i]} is at least as dressed as {All[i - 1]}");
        }
    }

    [Fact]
    public void EveryRarityHasItsOwnColor()
    {
        var colors = All.Select(r => RarityStyle.Of(r).Color).ToList();
        Assert.Equal(colors.Count, colors.Distinct().Count());
    }

    [Fact]
    public void TheTopRarityWearsEveryLayer()
    {
        var top = RarityStyle.Of(All[^1]);
        Assert.True(top.Glow > 0f);
        Assert.True(top.Sheen > 0f);
        Assert.True(top.Sparkle > 0f);
        Assert.True(top.PulseDepth > 0f);
    }
}
