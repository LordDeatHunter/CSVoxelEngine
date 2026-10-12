using System;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class EntityHealthbarTests
{
    private static float[] Fills(int health, int maxHealth)
    {
        Span<float> fills = stackalloc float[EntityHealthbarRenderer.HeartCount];
        EntityHealthbarRenderer.HeartFills(health, maxHealth, fills);
        return fills.ToArray();
    }

    [Fact]
    public void FullPoolFillsEveryHeart()
    {
        Assert.Equal([1f, 1f, 1f, 1f, 1f], Fills(8, 8));
    }

    [Fact]
    public void HalfPoolFillsTwoAndAHalfHearts()
    {
        Assert.Equal([1f, 1f, 0.5f, 0f, 0f], Fills(4, 8));
    }

    [Fact]
    public void SingleHitCropsOnlyTheFirstHeart()
    {
        // 7/8 of the pool across 5 hearts: 4 full plus a 3/8 sliver, not a half-heart step.
        var fills = Fills(7, 8);
        Assert.Equal(1f, fills[0], precision: 5);
        Assert.Equal(1f, fills[3], precision: 5);
        Assert.Equal(0.375f, fills[4], precision: 5);
    }

    [Fact]
    public void EmptyPoolEmptiesEveryHeart()
    {
        Assert.Equal([0f, 0f, 0f, 0f, 0f], Fills(0, 8));
    }

    [Fact]
    public void OutOfRangeHealthClamps()
    {
        Assert.Equal([0f, 0f, 0f, 0f, 0f], Fills(-3, 8));
        Assert.Equal([1f, 1f, 1f, 1f, 1f], Fills(99, 8));
    }

    [Fact]
    public void NonPositivePoolFillsNothing()
    {
        Assert.Equal([0f, 0f, 0f, 0f, 0f], Fills(5, 0));
        Assert.Equal([0f, 0f, 0f, 0f, 0f], Fills(5, -2));
    }

    [Fact]
    public void WrongSizedSpanThrows()
    {
        var fills = new float[EntityHealthbarRenderer.HeartCount + 1];
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EntityHealthbarRenderer.HeartFills(8, 8, fills));
    }

    [Fact]
    public void BarCentreFloatsAboveTheHead()
    {
        Assert.Equal(
            10f + Slime.HalfExtent + WorldGenerationConfig.EntityHealthbarLiftBlocks,
            EntityHealthbarRenderer.BarCentreY(10f, Slime.HalfExtent),
            precision: 5);
    }
}
