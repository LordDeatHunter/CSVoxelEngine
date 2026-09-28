using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <see cref="FogModel"/> mirrors the world fog in test-fragment.glsl. The rules: full fog at
/// FogFar whatever the heights or the wet blend (the LOD edge meets the sky), nothing at or inside
/// FogNear, distance fog only grows with distance, the wet path is exactly the old squared ramp,
/// the height layer is denser lower down, never passes its cap and is switched off when wet.
/// </summary>
public sealed class FogModelTests
{
    private const float Near = WorldGenerationConfig.FogNearBlocks;
    private const float Far = WorldGenerationConfig.FogFarBlocks;
    private const float Density = WorldGenerationConfig.FogDensity;
    private const float HDensity = WorldGenerationConfig.FogHeightDensity;
    private const float Falloff = WorldGenerationConfig.FogHeightFalloff;
    private const float HMax = WorldGenerationConfig.FogHeightMax;

    private static float Fog(float dist, float camY, float fragY, float wet) =>
        FogModel.Combine(
            FogModel.Distance(dist, Near, Far, Density, wet),
            FogModel.Height(dist, Near, camY, fragY, 0f, Falloff, HDensity, HMax),
            wet);

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(80f, 5f, 0f)]
    [InlineData(20f, 200f, 0.5f)]
    [InlineData(-30f, -40f, 1f)]
    public void FullFogAtFarRegardlessOfHeightAndWet(float camY, float fragY, float wet)
    {
        Assert.Equal(1f, Fog(Far, camY, fragY, wet), 5);
        Assert.Equal(1f, Fog(Far * 2f, camY, fragY, wet), 5);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(50f)]
    [InlineData(1f)]
    public void CombinedFogIsZeroAtOrBeforeNear(float camY)
    {
        foreach (var dist in new[] { 0f, Near * 0.5f, Near })
            Assert.Equal(0f, Fog(dist, camY, 0f, 0f), 6);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void DistanceFogIsMonotonic(float wet)
    {
        var previous = 0f;
        for (var dist = 0f; dist <= Far * 1.1f; dist += 10f)
        {
            var f = FogModel.Distance(dist, Near, Far, Density, wet);
            Assert.True(f >= previous - 1e-6f, $"fog fell from {previous} to {f} at {dist}");
            Assert.InRange(f, 0f, 1f);
            previous = f;
        }
    }

    [Fact]
    public void WetDistanceFogEqualsSquaredLinearRamp()
    {
        for (var dist = 0f; dist <= Far; dist += 37f)
        {
            var t = MathUtils.Clamp01((dist - Near) / (Far - Near));
            Assert.Equal(t * t, FogModel.Distance(dist, Near, Far, Density, 1f), 6);
        }
    }

    [Fact]
    public void LowerFragmentsGetMoreHeightFog()
    {
        const float dist = 700f;
        var previous = float.MaxValue;
        for (var fragY = 0f; fragY <= 120f; fragY += 10f)
        {
            var fh = FogModel.Height(dist, Near, 30f, fragY, 0f, Falloff, HDensity, 1f);
            Assert.True(fh < previous, $"height fog did not thin going up at {fragY}");
            previous = fh;
        }
    }

    [Fact]
    public void HeightFogNeverExceedsCap()
    {
        for (var dist = 0f; dist <= 100000f; dist *= 2f, dist += 1f)
            foreach (var y in new[] { -50f, 0f, 3f, 40f })
                Assert.InRange(FogModel.Height(dist, Near, y, y, 0f, Falloff, 1f, HMax), 0f, HMax);
    }

    [Fact]
    public void WetDisablesHeightFog()
    {
        var fd = FogModel.Distance(600f, Near, Far, Density, 1f);
        var fh = FogModel.Height(600f, Near, 0f, 0f, 0f, Falloff, HDensity, HMax);
        Assert.True(fh > 0f);
        Assert.Equal(fd, FogModel.Combine(fd, fh, 1f), 6);
        Assert.True(FogModel.Combine(fd, fh, 0f) > fd);
    }
}
