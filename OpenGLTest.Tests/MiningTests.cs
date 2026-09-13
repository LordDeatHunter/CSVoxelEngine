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
        Assert.Equal(400f, pickaxe.HitDelayMs);
        Assert.Equal(400f, ItemOf("primitive_axe").HitDelayMs);

        // A plain pickup declares no tool block, so it keeps the bare-hand baseline.
        var flint = ItemOf("flint");
        Assert.False(flint.IsTool);
        Assert.Equal(0.5f, flint.ToolPower);
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
        Assert.Equal(24f, log.Hardness);

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
        Assert.Equal(0.5f, Mining.HitDamage(dirt, null));
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
        Assert.Equal(0.6f, Mining.HitGapSeconds(null), precision: 4);
        Assert.Equal(0.6f, Mining.HitGapSeconds(ItemOf("flint")), precision: 4);
    }

    [Fact]
    public void NaturalWoodHardnessTapersFromTheFullWidthLog()
    {
        const float hardnessPerTaperLevel = 0.15f;
        var baseHp = ContentRegistry.RequireBlock("log").Hardness;
        Assert.Equal(24f, baseHp, precision: 3);

        foreach (var name in new[] { "branch", "limb", "sprig", "switch", "twig" })
        {
            var block = ContentRegistry.RequireBlock(name);
            var expected = baseHp * (1f - hardnessPerTaperLevel * block.WoodTaperLevel);
            Assert.Equal(expected, block.Hardness, precision: 3);
        }
    }

    [Fact]
    public void EntityDamageDefaultsToTheBareHandAndToolsOverride()
    {
        // Every item punches for the bare-hand baseline unless it declares entityDamage.
        Assert.Equal(1f, Mining.EntityDamage(null));
        Assert.Equal(1f, ItemOf("flint").EntityDamage);
        Assert.Equal(1, Combat.PunchDamage(null));

        // Tools are weapons too: primitive 3, copper 5, and the untiered blade 6.
        Assert.Equal(3f, ItemOf("primitive_pickaxe").EntityDamage);
        Assert.Equal(3f, ItemOf("primitive_axe").EntityDamage);
        Assert.Equal(5f, ItemOf("copper_pickaxe").EntityDamage);
        Assert.Equal(5f, ItemOf("copper_axe").EntityDamage);
        Assert.Equal(6f, ItemOf("obsidian_blade").EntityDamage);

        Assert.Equal(3, Combat.PunchDamage(ItemOf("primitive_pickaxe")));
        Assert.Equal(5, Combat.PunchDamage(ItemOf("copper_pickaxe")));
        Assert.Equal(6, Combat.PunchDamage(ItemOf("obsidian_blade")));
    }

    [Fact]
    public void BlockDamageIsTheItemsNumberWhilePowerOnlyGates()
    {
        // power is the mining tier; blockDamage is what a hit removes. The tools keep the damage
        // the old power value used to deal.
        var pickaxe = ItemOf("primitive_pickaxe");
        Assert.Equal(1f, pickaxe.ToolPower);
        Assert.Equal(1f, pickaxe.BlockDamage);
        Assert.Equal(1f, Mining.BlockDamage(pickaxe));
        Assert.Equal(1f, Mining.HitDamage(BlockOf("stone"), pickaxe));
        Assert.Equal(2f, Mining.BlockDamage(ItemOf("copper_pickaxe")));

        // A plain pickup and a bare hand fall back to the baseline.
        Assert.Equal(0.5f, Mining.BlockDamage(ItemOf("flint")));
        Assert.Equal(0.5f, Mining.BlockDamage(null));
        Assert.Equal(0.5f, Mining.HitDamage(BlockOf("dirt"), null));
    }
}
