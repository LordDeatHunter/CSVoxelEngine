using System;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Items;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Items;

/// <summary>
/// The mana draught (<see cref="ItemUseAction.RestoreMana"/> / <see cref="RestoreManaItem"/>): a drained
/// pool is refilled by the item's share and one unit is spent; a full pool refuses and spends nothing.
/// </summary>
public sealed class RestoreManaItemTests
{
    public RestoreManaItemTests() => TestContent.EnsureLoaded();

    private static RestoreManaItem Draught => (RestoreManaItem)ContentRegistry.RequireItem("mana_draught");

    [Fact]
    public void ADrainedPoolIsRefilledByTheShareAndOneUnitIsSpent()
    {
        var world = new StubItemUseWorld();
        world.Mana.TrySpend(world.Mana.Max, creative: false);

        var result = Draught.OnUse(new ItemUseContext(world, 4f, 0), null);

        Assert.True(result.Applied);
        Assert.Equal(Draught.Restores * world.Mana.Max, world.Mana.Current, 3);
        Assert.Equal(1, world.Consumed);
    }

    [Fact]
    public void AFullPoolRefusesTheUseAndSpendsNothing()
    {
        var world = new StubItemUseWorld();

        var result = Draught.OnUse(new ItemUseContext(world, 4f, 0), null);

        Assert.False(result.Applied);
        Assert.Equal(0, world.Consumed);
    }

    [Fact]
    public void TheRestoreNeverOverfillsThePool()
    {
        var mana = new PlayerMana();
        mana.TrySpend(1f, creative: false);
        Assert.True(mana.TryRestore(1f));
        Assert.Equal(mana.Max, mana.Current);
        Assert.False(mana.TryRestore(0.5f));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.5f)]
    [InlineData(float.NaN)]
    public void ANonPositiveShareRestoresNothing(float share)
    {
        var mana = new PlayerMana();
        mana.TrySpend(10f, creative: false);
        var before = mana.Current;
        Assert.False(mana.TryRestore(share));
        Assert.Equal(before, mana.Current);
    }

    [Fact]
    public void TheShareBelongsToTheRestoreActionAndMustBeAFraction()
    {
        const string path = "item.json";
        Assert.Throws<InvalidOperationException>(() =>
            ItemLoader.ParseRestoresMana(new ItemDefinition { RestoresMana = 0.4f }, ItemUseAction.Heal, path));
        Assert.Throws<InvalidOperationException>(() =>
            ItemLoader.ParseRestoresMana(new ItemDefinition(), ItemUseAction.RestoreMana, path));
        Assert.Throws<InvalidOperationException>(() =>
            ItemLoader.ParseRestoresMana(new ItemDefinition { RestoresMana = 1.5f }, ItemUseAction.RestoreMana, path));
        Assert.Equal(0.4f,
            ItemLoader.ParseRestoresMana(new ItemDefinition { RestoresMana = 0.4f }, ItemUseAction.RestoreMana, path));
    }
}
