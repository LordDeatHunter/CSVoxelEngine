using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class MiningTests
{
    public MiningTests() => TestContent.EnsureLoaded();

    private static Item ItemOf(string name) => ContentRegistry.RequireItem(name);

    private static byte BlockOf(string name) => ContentRegistry.RequireBlock(name).Id;

    [Fact]
    public void ToolPaceAndPowerComeFromTheItemWithBaselineFallback()
    {
        var pickaxe = ItemOf("primitive_pickaxe");
        var axe = ItemOf("primitive_axe");
        var flint = ItemOf("flint");
        Assert.True(pickaxe.IsTool);
        Assert.True(axe.IsTool);
        Assert.False(flint.IsTool);

        // A declared pace drives the hit gap; an item without one falls back to the baseline.
        Assert.True(pickaxe.HitDelayMs > 0f);
        Assert.Equal(pickaxe.HitDelayMs / 1000f, Mining.HitGapSeconds(pickaxe), precision: 5);
        Assert.Equal(Mining.DefaultHitGapMs, Mining.HitGapMs(flint), precision: 4);
        Assert.Equal(Mining.DefaultHitGapMs / 1000f, Mining.HitGapSeconds(null), precision: 5);

        // Same split for power and damage: tools carry their own numbers, the hand falls back.
        Assert.Equal(Mining.DefaultPower, Mining.Power(flint), precision: 5);
        Assert.Equal(Mining.DefaultPower, Mining.Power(null), precision: 5);
        Assert.Equal(Mining.DefaultBlockDamage, Mining.BlockDamage(flint), precision: 5);
        Assert.Equal(Mining.DefaultBlockDamage, Mining.BlockDamage(null), precision: 5);
        Assert.Equal(Mining.DefaultEntityDamage, Mining.EntityDamage(null), precision: 5);
    }

    [Fact]
    public void ToolTagsResolveThroughTheTagFiles()
    {
        Assert.True(ContentRegistry.IsInTag("pickaxe", ItemOf("primitive_pickaxe").Id));
        Assert.True(ContentRegistry.IsInTag("axe", ItemOf("primitive_axe").Id));
        Assert.False(ContentRegistry.IsInTag("pickaxe", ItemOf("primitive_axe").Id));
    }

    [Fact]
    public void GatedBlocksCarryAGateWhileUngatedBlocksCarryNone()
    {
        var stone = ContentRegistry.RequireBlock("stone");
        Assert.True(stone.RequiresTool);
        Assert.NotNull(stone.Tools);
        Assert.NotEmpty(stone.Tools!);
        Assert.True(stone.Hardness > 0f);

        var log = ContentRegistry.RequireBlock("oak_log");
        Assert.True(log.RequiresTool);
        Assert.NotNull(log.Tools);
        Assert.NotEmpty(log.Tools!);

        var dirt = ContentRegistry.RequireBlock("dirt");
        Assert.False(dirt.RequiresTool);
        Assert.Null(dirt.Tools);
    }

    [Fact]
    public void TheRightToolMinesAndTheWrongToolIsRefused()
    {
        var stone = BlockOf("stone");
        var log = BlockOf("oak_log");
        var pickaxe = ItemOf("primitive_pickaxe");
        var axe = ItemOf("primitive_axe");
        var flint = ItemOf("flint");

        Assert.True(Mining.CanMine(stone, pickaxe));
        Assert.Equal(Mining.BlockDamage(pickaxe), Mining.HitDamage(stone, pickaxe), precision: 5);

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
        Assert.Equal(Mining.DefaultBlockDamage, Mining.HitDamage(dirt, null), precision: 5);
    }

    [Fact]
    public void FluidCellsRefuseEveryTool()
    {
        var water = BlockOf("water");
        Assert.False(Mining.CanMine(water, ItemOf("primitive_pickaxe")));
        Assert.False(Mining.CanMine(water, null));
        Assert.Equal(0f, Mining.HitDamage(water, ItemOf("primitive_pickaxe")), precision: 5);
        Assert.Equal(0f, Mining.HitDamage(water, null), precision: 5);
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
    public void ThinnerWoodIsSofter()
    {
        var woods = BlockType.Types.Values.Where(t => BlockType.IsTreeWood(t.Id)).ToList();
        Assert.NotEmpty(woods);
        foreach (var wood in woods)
        {
            // Hardness never drops as the wood thickens, whatever the parent direction says,
            // and only the full cube carries the block's whole hardness
            for (var parent = 0; parent < WoodState.ParentDetached; parent++)
            {
                var previous = 0f;
                for (var t = 0; t <= WoodState.MaxThickness; t++)
                {
                    var hardness = Mining.HardnessOf(wood.Id, WoodState.Encode(t, parent, false));
                    Assert.True(hardness >= previous, $"{wood.Name} softens from thickness {t - 1} to {t}");
                    previous = hardness;
                }
            }

            Assert.Equal(wood.Hardness, Mining.HardnessOf(wood.Id, WoodState.Encode(WoodState.FullCube, 0, false)), precision: 4);
            Assert.Equal(wood.Hardness, Mining.HardnessOf(wood.Id, WoodState.Detached), precision: 4);
        }

        // Anything that is not tree wood ignores the state byte
        var stone = ContentRegistry.RequireBlock("stone");
        for (var state = 0; state < 256; state += 17)
            Assert.Equal(stone.Hardness, Mining.HardnessOf(stone.Id, (byte)state), precision: 5);
    }

    [Fact]
    public void WeaponsHitHarderThanFistsWithTheBladeOnTop()
    {
        var bareHand = Mining.EntityDamage(null);
        Assert.Equal(Mining.DefaultEntityDamage, bareHand, precision: 5);
        Assert.Equal(bareHand, ItemOf("flint").EntityDamage, precision: 5);
        Assert.Equal((int)bareHand, Combat.PunchDamage(null));

        var primitive = ItemOf("primitive_pickaxe").EntityDamage;
        var copper = ItemOf("copper_pickaxe").EntityDamage;
        var blade = ItemOf("obsidian_blade").EntityDamage;
        Assert.True(primitive > bareHand);
        Assert.True(copper > primitive);
        Assert.True(blade >= copper);

        Assert.Equal((int)primitive, Combat.PunchDamage(ItemOf("primitive_pickaxe")));
        Assert.Equal((int)copper, Combat.PunchDamage(ItemOf("copper_pickaxe")));
        Assert.Equal((int)blade, Combat.PunchDamage(ItemOf("obsidian_blade")));
    }

    [Fact]
    public void DamageComesFromTheItemWhilePowerOnlyGates()
    {
        var pickaxe = ItemOf("primitive_pickaxe");
        Assert.Equal(pickaxe.BlockDamage, Mining.BlockDamage(pickaxe), precision: 5);
        Assert.True(Mining.BlockDamage(ItemOf("copper_pickaxe")) > Mining.BlockDamage(pickaxe));
        Assert.Equal(Mining.DefaultBlockDamage, Mining.BlockDamage(ItemOf("flint")), precision: 5);
        Assert.Equal(Mining.DefaultBlockDamage, Mining.BlockDamage(null), precision: 5);

        Assert.Equal(Mining.BlockDamage(pickaxe), Mining.HitDamage(BlockOf("stone"), pickaxe), precision: 5);
        Assert.Equal(0f, Mining.HitDamage(BlockOf("stone"), ItemOf("primitive_axe")), precision: 5);
        Assert.Equal(Mining.DefaultBlockDamage, Mining.HitDamage(BlockOf("dirt"), null), precision: 5);
    }
}
