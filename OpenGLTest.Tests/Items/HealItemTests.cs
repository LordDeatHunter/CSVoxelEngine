using System;
using System.Linq;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Items;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Items;

/// <summary>
/// The bandage's heal (<see cref="ItemUseAction.Heal"/> / <see cref="HealItem"/>): a hurt player
/// is healed by <see cref="PlayerConfig.BandageHealAmount"/> and one unit is spent; a
/// full pool refuses the use and spends nothing.
/// </summary>
public sealed class HealItemTests
{
    public HealItemTests() => TestContent.EnsureLoaded();

    private static ushort Bandage => ContentRegistry.RequireItem("bandage").Id;

    [Fact]
    public void AHurtPlayerIsHealedAndOneBandageIsSpent()
    {
        var world = new StubItemUseWorld { Health = 40 };
        var bandage = ContentRegistry.RequireItem("bandage");
        var result = bandage.OnUse(new ItemUseContext(world, 4f, 0), null);

        Assert.True(result.Applied);
        Assert.Equal(40 + PlayerConfig.BandageHealAmount, world.Health);
        Assert.Equal(1, world.Consumed);
    }

    [Fact]
    public void AFullPoolRefusesTheUseAndSpendsNothing()
    {
        var world = new StubItemUseWorld();
        var bandage = ContentRegistry.RequireItem("bandage");
        var result = bandage.OnUse(new ItemUseContext(world, 4f, 0), null);

        Assert.False(result.Applied);
        Assert.Equal(PlayerHealth.MaxHealth, world.Health);
        Assert.Equal(0, world.Consumed);
    }

    [Fact]
    public void AHealItemHealsItsOwnAmountAndOneWithoutTakesTheBandages()
    {
        // Every shipped heal item restores exactly what it declares (or the bandage's amount with none)
        var healers = ItemRegistry.All.OfType<HealItem>().ToArray();
        Assert.Contains(healers, h => h.Heals != PlayerConfig.BandageHealAmount);
        foreach (var item in healers)
        {
            var world = new StubItemUseWorld { Health = 1 };
            Assert.True(item.OnUse(new ItemUseContext(world, 4f, 0), null).Applied);
            Assert.Equal(1 + item.Heals, world.Health);
            Assert.Equal(1, world.Consumed);
        }
        Assert.Equal(PlayerConfig.BandageHealAmount, Assert.IsType<HealItem>(ContentRegistry.RequireItem("bandage")).Heals);
    }

    [Fact]
    public void HealsBelongsToAHealItemAndMustBePositive()
    {
        const string path = "assets/data/items/broken.json";
        var onANonHealer = new ItemDefinition { Heals = 5 };
        var error = Assert.Throws<InvalidOperationException>(() => ItemLoader.ParseHeals(onANonHealer, ItemUseAction.None, path));
        Assert.Contains(path, error.Message);

        var zero = new ItemDefinition { Heals = 0 };
        error = Assert.Throws<InvalidOperationException>(() => ItemLoader.ParseHeals(zero, ItemUseAction.Heal, path));
        Assert.Contains(path, error.Message);

        Assert.Equal(0, ItemLoader.ParseHeals(new ItemDefinition(), ItemUseAction.None, path));
        Assert.Equal(PlayerConfig.BandageHealAmount, ItemLoader.ParseHeals(new ItemDefinition(), ItemUseAction.Heal, path));
        Assert.Equal(7, ItemLoader.ParseHeals(new ItemDefinition { Heals = 7 }, ItemUseAction.Heal, path));
    }

    [Fact]
    public void TheHealNeverOverfillsThePool()
    {
        var health = new PlayerHealth();
        health.TakeDamage(5);
        Assert.True(health.Heal(PlayerConfig.BandageHealAmount));
        Assert.Equal(PlayerHealth.MaxHealth, health.Health);

        // A dead pool is not healed back to life, and a full one refuses
        var dead = new PlayerHealth();
        dead.TakeDamage(PlayerHealth.MaxHealth);
        Assert.False(dead.Heal(PlayerConfig.BandageHealAmount));
        Assert.False(new PlayerHealth().Heal(PlayerConfig.BandageHealAmount));
    }

    [Fact]
    public void TheBandageCraftsFromCotton()
    {
        var recipe = ContentRegistry.RecipeForResult(Bandage);
        Assert.NotNull(recipe);
        Assert.Equal(1, recipe!.ResultCount);
        var cotton = ContentRegistry.RequireItem("cotton").Id;
        Assert.Contains(recipe.Ingredients, i => i.Accepts(cotton));
    }
}
