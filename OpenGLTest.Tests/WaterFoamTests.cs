using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The shore foam in test-fragment.glsl rises as the water the eye looks through (the
/// refraction path's depth from the surface to the scene behind it) thins below
/// <see cref="WorldGenerationConfig.WaterFoamWidthBlocks"/>. Looking straight down is the
/// shortest that path ever gets, so the band stays off open water exactly when the width is
/// below the shallowest a full water cell over a block floor can read: its surface height less
/// the swell the vertex shader lowers the surface by.
/// </summary>
public sealed class WaterFoamTests
{
    /// <summary>test-vertex.glsl's WaveHeight keeps its four sines' sum under this, so a full
    /// cell's surface never pokes out of its block.</summary>
    private const float WaveSwellCeiling = 0.09f;

    [Fact]
    public void OpenWaterOverABlockFloorNeverFoams()
    {
        Assert.True(WorldGenerationConfig.WaterFoamWidthBlocks > 0f, "the foam band is switched off");
        Assert.True(WorldGenerationConfig.WaterFoamWidthBlocks < FluidState.SourceHeight - WaveSwellCeiling,
            "a band this wide foams over every one-block-deep pool, not just at its shore");
        // The shader mixes the foam over the water by its opacity, so it must stay a blend weight
        Assert.InRange(WorldGenerationConfig.WaterFoamStrength, 0f, 1f);
    }
}
