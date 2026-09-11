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
}
