using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The ice glint (<see cref="IceGlint"/>, the twin of the ice tile term in test-fragment.glsl): a sky
/// sheen that grows toward a graze and a sun highlight that only lit ice shows.
/// </summary>
public sealed class IceGlintTests
{
    private const float DaySun = 2f;

    [Fact]
    public void TheSkySheenGrowsTowardAGraze()
    {
        var previous = IceGlint.SheenWeight(1f);
        for (var cos = 1f; cos >= 0f; cos -= 0.01f)
        {
            var w = IceGlint.SheenWeight(cos);
            Assert.True(w >= previous - 1e-6f, $"sheen fell from {previous} to {w} toward a graze ({cos})");
            previous = w;
        }

        Assert.True(IceGlint.SheenWeight(0f) > IceGlint.SheenWeight(1f));
        Assert.InRange(IceGlint.SheenWeight(0f), 0f, WorldGenerationConfig.IceSheenStrength + 1e-6f);
    }

    [Fact]
    public void TheHighlightGrowsAsTheHalfVectorLinesUpWithTheFace()
    {
        var previous = IceGlint.Highlight(-1f, 0.5f, DaySun, 1f);
        for (var ndh = -1f; ndh <= 1f; ndh += 0.002f)
        {
            var h = IceGlint.Highlight(ndh, 0.5f, DaySun, 1f);
            Assert.True(h >= previous, $"highlight fell from {previous} to {h} at n.h {ndh}");
            previous = h;
        }

        Assert.Equal(WorldGenerationConfig.IceGlintStrength, IceGlint.Highlight(1f, 0.5f, DaySun, 1f), 5);
    }

    [Fact]
    public void IceInShadowOrFacingAwayFromTheSunHasNoHighlight()
    {
        Assert.Equal(0f, IceGlint.Highlight(1f, 0.5f, DaySun, 0f));
        Assert.Equal(0f, IceGlint.Highlight(1f, 0f, DaySun, 1f));
        Assert.Equal(0f, IceGlint.Highlight(1f, -0.4f, DaySun, 1f));
        Assert.Equal(0f, IceGlint.Highlight(1f, 0.5f, 0f, 1f));
    }

    [Fact]
    public void TheHighlightNeverExceedsItsStrength()
    {
        for (var ndh = -1f; ndh <= 1f; ndh += 0.1f)
        for (var ndl = -1f; ndl <= 1f; ndl += 0.1f)
        for (var reach = 0f; reach <= 1.5f; reach += 0.25f)
            Assert.InRange(IceGlint.Highlight(ndh, ndl, DaySun, reach), 0f, WorldGenerationConfig.IceGlintStrength + 1e-6f);
    }
}
