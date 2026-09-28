using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <see cref="SurfaceFresnel"/> mirrors the wet waterline in test-fragment.glsl. The rules: the
/// fresnel only ever grows toward a graze, a submerged face is fully wet, the wet band fades out
/// monotonically with height above its surface, and an unflagged face never darkens.
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
}
