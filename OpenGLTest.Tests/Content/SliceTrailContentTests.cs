using System;
using System.Linq;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Items;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// The slice trail's content side: only a blade carries a trail color, the color a blade's
/// swing is drawn with is opaque, and the blade a client assumes for a remote player's swing is
/// picked deterministically.
/// </summary>
public sealed class SliceTrailContentTests
{
    public SliceTrailContentTests() => TestContent.EnsureLoaded();

    private static bool IsBlade(Item item) => item.Attack is AttackPattern.Slice or AttackPattern.Sword;

    [Fact]
    public void EveryItemWithATrailColorHasABladeAttack()
    {
        foreach (var item in ItemRegistry.All.Where(i => i.TrailColor.HasValue))
            Assert.True(IsBlade(item), $"{item.SourceName} has a trail color but attack {item.Attack}.");
    }

    [Theory]
    [InlineData(AttackPattern.Slice)]
    [InlineData(AttackPattern.Sword)]
    public void FindByAttackReturnsTheLowestIdItemOfThatPattern(AttackPattern attack)
    {
        var candidates = ItemRegistry.All.Where(i => i is not BlockItem && i.Attack == attack).ToList();
        Assert.NotEmpty(candidates);

        var found = ItemRegistry.FindByAttack(attack);

        Assert.NotNull(found);
        Assert.Equal(attack, found.Attack);
        Assert.Equal(candidates.Min(i => i.Id), found.Id);
    }

    [Fact]
    public void BladeTrailColorsAreOpaque()
    {
        foreach (var item in ItemRegistry.All.Where(IsBlade))
            Assert.Equal(255, item.TrailColorOrDefault.A);
    }

    [Fact]
    public void ATrailOnAPunchItemIsRefused()
    {
        var trail = new ItemDefinition.TrailDefinition { Color = [10, 20, 30] };

        var e = Assert.Throws<InvalidOperationException>(
            () => ItemLoader.ParseTrail(trail, AttackPattern.Punch, "data/items/x.json"));
        Assert.Contains("data/items/x.json", e.Message);
        Assert.NotNull(ItemLoader.ParseTrail(trail, AttackPattern.Slice, "data/items/x.json"));
    }

    [Theory]
    [InlineData(new[] { 1, 2 })]
    [InlineData(new[] { 1, 2, 256 })]
    [InlineData(new[] { -1, 2, 3 })]
    public void AMalformedTrailColorIsRefused(int[] color)
    {
        var trail = new ItemDefinition.TrailDefinition { Color = color };

        Assert.Throws<InvalidOperationException>(
            () => ItemLoader.ParseTrail(trail, AttackPattern.Sword, "data/items/x.json"));
    }
}
