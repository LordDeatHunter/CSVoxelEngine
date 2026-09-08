using System.IO;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class WaterTests
{
    [Fact]
    public void WaterShadersExist()
    {
        var root = TestContent.FindAssetsRoot();
        Assert.True(File.Exists(Path.Combine(root, "shaders", "test-vertex.glsl")));
        Assert.True(File.Exists(Path.Combine(root, "shaders", "test-fragment.glsl")));
        Assert.True(File.Exists(Path.Combine(root, "shaders", "rays-vertex.glsl")));
        Assert.True(File.Exists(Path.Combine(root, "shaders", "rays-fragment.glsl")));
    }

    [Fact]
    public void WaveAmplitudeStaysInsideTheCell()
    {
        // The four amplitudes of WaveHeight in test-vertex.glsl (and, differentiated, of
        // WaveNormal in test-fragment.glsl). A full fluid cell's surface sits at 8/9 of its
        // block, so the crest of the sum must not push it through the cell above — retune
        // these and the GLSL together, or the sea starts poking out of its own blocks.
        const float sum = 0.035f + 0.025f + 0.015f + 0.010f;
        Assert.True(8f / 9f + sum < 1f, $"wave crest {8f / 9f + sum} reaches out of the cell");
    }
}
