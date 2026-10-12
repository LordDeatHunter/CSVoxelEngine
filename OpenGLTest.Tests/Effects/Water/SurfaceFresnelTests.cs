using VoxelEngine.Effects.Water;
using Xunit;

namespace VoxelEngine.Tests.Effects.Water;

/// <summary>
/// <see cref="SurfaceFresnel"/> mirrors the wet waterline in test-fragment.glsl. The rules: the
/// fresnel only ever grows toward a graze, a submerged fragment is wet at or below its own surface
/// and dry above it, and a dry face never darkens at any height.
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
}
