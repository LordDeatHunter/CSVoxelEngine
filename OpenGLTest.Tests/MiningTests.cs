using Xunit;

namespace silkgltriangle.Tests;

public sealed class MiningTests
{
    public MiningTests() => TestContent.EnsureLoaded();

    private static Item ItemOf(string name) => ContentRegistry.RequireItem(name);

    private static byte BlockOf(string name) => ContentRegistry.RequireBlock(name).Id;

    [Fact]
    public void ToolItemsExposeTheirPowerAndPace()
    {
        var pickaxe = ItemOf("primitive_pickaxe");
        Assert.True(pickaxe.IsTool);
        Assert.Equal(1f, pickaxe.ToolPower);
        Assert.Equal(Mining.DefaultHitGapMs, pickaxe.HitDelayMs);

        // A plain pickup declares no tool block, so it keeps the bare-hand baseline.
        var flint = ItemOf("flint");
        Assert.False(flint.IsTool);
        Assert.Equal(1f, flint.ToolPower);
        Assert.Equal(Mining.DefaultHitGapMs, flint.HitDelayMs);
    }

    [Fact]
    public void ToolTagsResolveThroughTheTagFiles()
    {
        Assert.True(ContentRegistry.IsInTag("pickaxe", ItemOf("primitive_pickaxe").Id));
        Assert.True(ContentRegistry.IsInTag("axe", ItemOf("primitive_axe").Id));
        Assert.False(ContentRegistry.IsInTag("pickaxe", ItemOf("primitive_axe").Id));
    }

    [Fact]
    public void RequiresToolBlocksParseTheirGateAndFloatHardness()
    {
        var stone = ContentRegistry.RequireBlock("stone");
        Assert.True(stone.RequiresTool);
        Assert.NotNull(stone.Tools);
        Assert.Equal(1f, stone.Tools!["pickaxe"]);
        Assert.Equal(5f, stone.Hardness);

        var log = ContentRegistry.RequireBlock("log");
        Assert.True(log.RequiresTool);
        Assert.Equal(1f, log.Tools!["axe"]);
        Assert.Equal(8f, log.Hardness);

        // An ungated block carries no gate at all.
        var dirt = ContentRegistry.RequireBlock("dirt");
        Assert.False(dirt.RequiresTool);
        Assert.Null(dirt.Tools);
    }

    [Fact]
    public void TheRightToolMinesAndTheWrongToolIsRefused()
    {
        var stone = BlockOf("stone");
        var log = BlockOf("log");
        var pickaxe = ItemOf("primitive_pickaxe");
        var axe = ItemOf("primitive_axe");
        var flint = ItemOf("flint");

        Assert.True(Mining.CanMine(stone, pickaxe));
        Assert.Equal(1f, Mining.HitDamage(stone, pickaxe));

        Assert.False(Mining.CanMine(stone, axe));
        Assert.Equal(0f, Mining.HitDamage(stone, axe));
        Assert.False(Mining.CanMine(stone, flint));
        Assert.False(Mining.CanMine(stone, null));

        Assert.True(Mining.CanMine(log, axe));
        Assert.False(Mining.CanMine(log, pickaxe));
    }

    [Fact]
    public void UngatedBlocksAcceptAnyToolOrHand()
    {
        var dirt = BlockOf("dirt");
        Assert.True(Mining.CanMine(dirt, null));
        Assert.True(Mining.CanMine(dirt, ItemOf("primitive_pickaxe")));
        Assert.Equal(1f, Mining.HitDamage(dirt, null));
    }

    [Fact]
    public void PowerThresholdsAreInclusive()
    {
        Assert.True(Mining.MeetsRequired(1f, 1f));
        Assert.True(Mining.MeetsRequired(1.5f, 1f));
        Assert.False(Mining.MeetsRequired(0.99f, 1f));
        Assert.True(Mining.MeetsRequired(0f, 0f));
    }

    [Fact]
    public void HitGapDefaultsToTheBaseline()
    {
        Assert.Equal(0.2f, Mining.HitGapSeconds(null), precision: 4);
        Assert.Equal(0.2f, Mining.HitGapSeconds(ItemOf("flint")), precision: 4);
    }
}
