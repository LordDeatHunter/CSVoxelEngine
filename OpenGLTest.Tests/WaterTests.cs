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
        Assert.True(File.Exists(Path.Combine(root, "shaders", "rays-upsample-fragment.glsl")));
    }

    // The underwater rays pass marches at half resolution and composites back through
    // rays-upsample-fragment.glsl. PERF_TODO's "Not chasing" list rules out cutting shader ALU
    // in this pass (the caustic Voronoi net, the 8 point lights, the 4-tap PCF) and records
    // resolution as the only allowed lever, so the march's step count, sample count and
    // per-sample formula must survive the half-res round untouched. These two Facts pin the
    // file contents and that scope boundary - not rendered output. The perceptual result of the
    // half-res round is only reachable through the scripted visual run, never headlessly.
    [Fact]
    public void RayMarchStaysUnchangedByTheHalfResRound()
    {
        var march = File.ReadAllText(
            Path.Combine(TestContent.FindAssetsRoot(), "shaders", "rays-fragment.glsl"));
        // The loop header: the step count is still driven by uSteps, one sample per iteration.
        Assert.Contains("for (int i = 0; i < uSteps; i++) {", march);
        // The per-sample accumulation: shadow lookup times depth falloff times distance falloff.
        Assert.Contains(
            "accum += Lit(p) * exp(-0.08 * below) * exp(-0.03 * t) * stepLen;", march);
    }

    [Fact]
    public void UpsampleShaderIsDepthAware()
    {
        var upsample = File.ReadAllText(
            Path.Combine(TestContent.FindAssetsRoot(), "shaders", "rays-upsample-fragment.glsl"));
        // Reads the half-res march output...
        Assert.Contains("uniform sampler2D uRays", upsample);
        // ...and the full-res scene depth, so it can reject taps from the far side of a silhouette.
        Assert.Contains("uniform sampler2D uSceneDepth", upsample);
        // Explicit texel fetches, not a plain bilinear texture() - that is what makes the 2x2
        // neighbourhood individually reweightable instead of pre-blended by the sampler.
        Assert.Contains("texelFetch", upsample);
    }
}
