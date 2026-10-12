using VoxelEngine.Content.Blocks;
using VoxelEngine.Items;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Items;

/// <summary>
/// The bandage's heal (<see cref="ItemUseAction.Heal"/> / <see cref="HealItem"/>): a hurt player
/// is healed by <see cref="WorldGenerationConfig.BandageHealAmount"/> and one unit is spent; a
/// full pool refuses the use and spends nothing.
/// </summary>
public sealed class HealItemTests
{
    public HealItemTests() => TestContent.EnsureLoaded();

    private static byte Bandage => ContentRegistry.RequireItem("bandage").Id;

    [Fact]
    public void AHurtPlayerIsHealedAndOneBandageIsSpent()
    {
        var world = new StubItemUseWorld { Health = 40 };
        var bandage = ContentRegistry.RequireItem("bandage");
        var result = bandage.OnUse(new ItemUseContext(world, 4f, 0), null);

        Assert.True(result.Applied);
        Assert.Equal(40 + WorldGenerationConfig.BandageHealAmount, world.Health);
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
    public void TheHealNeverOverfillsThePool()
    {
        var health = new PlayerHealth();
        health.TakeDamage(5);
        Assert.True(health.Heal(WorldGenerationConfig.BandageHealAmount));
        Assert.Equal(PlayerHealth.MaxHealth, health.Health);

        // A dead pool is not healed back to life, and a full one refuses
        var dead = new PlayerHealth();
        dead.TakeDamage(PlayerHealth.MaxHealth);
        Assert.False(dead.Heal(WorldGenerationConfig.BandageHealAmount));
        Assert.False(new PlayerHealth().Heal(WorldGenerationConfig.BandageHealAmount));
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
