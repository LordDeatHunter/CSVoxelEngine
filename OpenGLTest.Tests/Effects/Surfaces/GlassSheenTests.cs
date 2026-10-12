using System.Numerics;
using VoxelEngine.Effects.Surfaces;
using Xunit;

namespace VoxelEngine.Tests.Effects.Surfaces;

/// <summary>
/// The glass sheen (<see cref="GlassSheen"/>, the twin of the glass tile term in test-fragment.glsl):
/// a grazing sky tint folded into the translucent blend so it shows on see-through glass.
/// </summary>
public sealed class GlassSheenTests
{
    private static readonly Vector3 Sky = new(0.3f, 0.5f, 0.9f);

    [Fact]
    public void TheSheenGrowsTowardAGrazeOnEitherSideOfTheFace()
    {
        var previous = GlassSheen.Weight(1f);
        for (var cos = 1f; cos >= 0f; cos -= 0.01f)
        {
            var w = GlassSheen.Weight(cos);
            Assert.True(w >= previous - 1e-6f, $"sheen fell from {previous} to {w} toward a graze ({cos})");
            Assert.Equal(w, GlassSheen.Weight(-cos), 6);
            previous = w;
        }

        Assert.True(GlassSheen.Weight(0f) > 4f * GlassSheen.Weight(1f));
    }

    [Fact]
    public void FoldingTheSkyIntoTheBlendIsTheGlassMixedTowardTheSky()
    {
        // Over any background, the folded color and alpha composite to exactly the plain glass
        // composite mixed toward the sky by the sheen weight: the sheen shows even where the glass
        // texel is nearly transparent.
        var glass = new Vector3(0.6f, 0.7f, 0.7f);
        foreach (var background in new[] { Vector3.Zero, Vector3.One, new Vector3(0.9f, 0.4f, 0.1f) })
        foreach (var texAlpha in new[] { 0.1f, 0.25f, 0.6f, 1f })
        foreach (var cos in new[] { 1f, 0.5f, 0.2f, 0.02f })
        {
            var f = GlassSheen.Weight(cos);
            var alpha = GlassSheen.Alpha(texAlpha, f);
            var folded = GlassSheen.Over(GlassSheen.Color(glass, texAlpha, Sky, f), alpha, background);
            var expected = Vector3.Lerp(GlassSheen.Over(glass, texAlpha, background), Sky, f);
            Assert.Equal(expected.X, folded.X, 4);
            Assert.Equal(expected.Y, folded.Y, 4);
            Assert.Equal(expected.Z, folded.Z, 4);
        }
    }

    [Fact]
    public void TheBlendedAlphaOnlyRisesAndStaysAFraction()
    {
        for (var a = 0f; a <= 1f; a += 0.05f)
        for (var f = 0f; f <= 1f; f += 0.05f)
        {
            var alpha = GlassSheen.Alpha(a, f);
            Assert.True(alpha >= a - 1e-6f, $"alpha fell from {a} to {alpha}");
            Assert.InRange(alpha, 0f, 1f + 1e-6f);
        }
    }

    [Fact]
    public void NoSheenLeavesTheGlassAlone()
    {
        var glass = new Vector3(0.6f, 0.7f, 0.7f);
        foreach (var a in new[] { 0.1f, 0.5f, 1f })
        {
            Assert.Equal(a, GlassSheen.Alpha(a, 0f));
            var c = GlassSheen.Color(glass, a, Sky, 0f);
            Assert.Equal(glass.X, c.X, 5);
            Assert.Equal(glass.Y, c.Y, 5);
            Assert.Equal(glass.Z, c.Z, 5);
        }
    }
}
