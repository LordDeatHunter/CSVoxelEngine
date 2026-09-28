using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <see cref="SurfaceFresnel"/> mirrors the wet waterline in test-fragment.glsl. The rules: the
/// fresnel only ever grows toward a graze, a submerged face is fully wet, the wet band fades out
/// monotonically with height above its surface, an unflagged face never darkens, and the
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
    [InlineData(false, 0f)]
    [InlineData(true, 0f)]
    [InlineData(true, 3f)]
    public void SubmergedIsFullyWet(bool alsoWetBand, float height)
    {
        Assert.Equal(1f, SurfaceFresnel.WetWeight(true, alsoWetBand, height, 0.5f));
    }

    [Fact]
    public void WetBandFadesOutWithHeight()
    {
        const float band = 0.5f;
        Assert.Equal(1f, SurfaceFresnel.WetWeight(false, true, 0f, band));
        var previous = 1f;
        for (var i = 1; i <= 10; i++)
        {
            var w = SurfaceFresnel.WetWeight(false, true, band * i / 10f, band);
            Assert.True(w < previous, $"wet weight did not fall at {i}/10 of the band");
            previous = w;
        }
        Assert.Equal(0f, previous);
        Assert.Equal(0f, SurfaceFresnel.WetWeight(false, true, band * 2f, band));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.2f)]
    [InlineData(-1f)]
    public void DryFaceIsNotWet(float height)
    {
        Assert.Equal(0f, SurfaceFresnel.WetWeight(false, false, height, 0.5f));
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
