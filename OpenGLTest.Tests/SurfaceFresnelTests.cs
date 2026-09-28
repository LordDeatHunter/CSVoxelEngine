using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <see cref="SurfaceFresnel"/> mirrors the wet waterline in test-fragment.glsl. The rules: the
/// fresnel only ever grows toward a graze, a submerged fragment is wet at or below its own surface
/// and dry above it, a dry face never darkens at any height, and the
/// universal rim is nothing head-on or on a back face, everything at a graze and gone past its
/// fade distance.
/// </summary>
public sealed class SurfaceFresnelTests
{
    [Fact]
    public void SchlickFallsFromGrazingToHeadOn()
    {
        var previous = SurfaceFresnel.Schlick(0f);
        Assert.Equal(1f, previous, 5);
        for (var i = 1; i <= 20; i++)
        {
            var f = SurfaceFresnel.Schlick(i / 20f);
            Assert.True(f <= previous, $"fresnel rose at cos {i / 20f}");
            previous = f;
        }
        Assert.Equal(SurfaceFresnel.F0, previous, 5);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.01f)]
    [InlineData(-3f)]
    public void SubmergedFragmentAtOrBelowItsSurfaceIsFullyWet(float heightAboveSurface)
    {
        Assert.Equal(1f, SurfaceFresnel.WetWeight(true, heightAboveSurface));
    }

    [Theory]
    [InlineData(0.01f)]
    [InlineData(0.25f)]
    [InlineData(3f)]
    public void SubmergedFragmentAboveItsOwnSurfaceIsDry(float heightAboveSurface)
    {
        Assert.Equal(0f, SurfaceFresnel.WetWeight(true, heightAboveSurface));
    }

    [Theory]
    [InlineData(-3f)]
    [InlineData(0f)]
    [InlineData(0.2f)]
    [InlineData(3f)]
    public void DryFaceIsNeverWet(float heightAboveSurface)
    {
        Assert.Equal(0f, SurfaceFresnel.WetWeight(false, heightAboveSurface));
    }

    [Fact]
    public void RimIsZeroHeadOnAndFullAtGrazing()
    {
        Assert.Equal(0f, SurfaceFresnel.Rim(1f), 5);
        Assert.Equal(1f, SurfaceFresnel.Rim(1e-5f), 3);
        var previous = 1f;
        for (var i = 1; i <= 20; i++)
        {
            var r = SurfaceFresnel.Rim(i / 20f);
            Assert.InRange(r, 0f, 1f);
            Assert.True(r <= previous, $"rim rose at cos {i / 20f}");
            previous = r;
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.01f)]
    [InlineData(-1f)]
    public void BackFaceGetsNoRim(float cosTheta)
    {
        Assert.Equal(0f, SurfaceFresnel.Rim(cosTheta));
    }

    [Theory]
    [InlineData(8f, 32f)]
    [InlineData(2f, 5f)]
    public void RimFadeVanishesPastFar(float near, float far)
    {
        Assert.Equal(1f, SurfaceFresnel.RimFade(0f, near, far));
        Assert.Equal(1f, SurfaceFresnel.RimFade(near, near, far));
        var mid = SurfaceFresnel.RimFade(0.5f * (near + far), near, far);
        Assert.True(mid > 0f && mid < 1f);
        Assert.Equal(0f, SurfaceFresnel.RimFade(far, near, far));
        Assert.Equal(0f, SurfaceFresnel.RimFade(far * 4f, near, far));
    }
}
