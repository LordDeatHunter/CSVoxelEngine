using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The giant slime (<see cref="SlimeSize.Max"/>): a ring spawn rolls it less often than the size below
/// it in every light, yet it stays possible at night and underground.
/// </summary>
public sealed class GiantSlimeTests
{
    // Shares of each rolled size over the same evenly spread rolls, so conditions compare like for like.
    private static float Share(float night, bool underground, int size)
    {
        const int count = 100_000;
        var hits = 0;
        for (var i = 0; i < count; i++)
            if (SlimeSize.Roll(night, underground, (i + 0.5f) / count) == size)
                hits++;
        return hits / (float)count;
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(0.5f, false)]
    [InlineData(1f, false)]
    [InlineData(0f, true)]
    public void TheGiantIsRarerThanTheSizeBelowIt(float night, bool underground)
    {
        var giant = Share(night, underground, SlimeSize.Max);
        var below = Share(night, underground, SlimeSize.Max - 1);
        Assert.True(giant < below, $"giants {giant:P3} vs the size below {below:P3}");
    }

    [Fact]
    public void AGiantStaysPossibleAtNightAndUnderground()
    {
        Assert.True(Share(1f, false, SlimeSize.Max) > 0f, "no giant ever spawns at night");
        Assert.True(Share(0f, true, SlimeSize.Max) > 0f, "no giant ever spawns underground");
        // and never more often by day than at night
        Assert.True(Share(0f, false, SlimeSize.Max) <= Share(1f, false, SlimeSize.Max));
    }
}
