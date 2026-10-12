using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The deep-water darkness curve (<see cref="DeepWaterFog"/>, uploaded as test-fragment.glsl's
/// <c>uDeepWaterDim</c>): the underwater fog darkens and loses red the deeper the eye is.
/// </summary>
public sealed class DeepWaterFogTests
{
    [Fact]
    public void TheFogIsUntouchedAtOrAboveTheSurface()
    {
        foreach (var depth in new[] { -10f, -0.5f, 0f, float.NaN })
            Assert.Equal(Vector3.Zero, DeepWaterFog.Dim(depth));
        Assert.Equal(Vector3.One, DeepWaterFog.Tint(0f));
    }

    [Fact]
    public void TheFogDarkensSteadilyWithDepth()
    {
        var previous = DeepWaterFog.Dim(0f);
        for (var depth = 0f; depth <= 200f; depth += 0.25f)
        {
            var d = DeepWaterFog.Dim(depth);
            Assert.True(d.X >= previous.X && d.Y >= previous.Y && d.Z >= previous.Z,
                $"the dim fell from {previous} to {d} at {depth} blocks");
            previous = d;
        }

        Assert.True(DeepWaterFog.Dim(30f).Y > DeepWaterFog.Dim(3f).Y);
    }

    [Fact]
    public void RedIsLostFirst()
    {
        for (var depth = 0.25f; depth <= 200f; depth += 0.25f)
        {
            var d = DeepWaterFog.Dim(depth);
            Assert.True(d.X >= d.Y && d.Y >= d.Z, $"at {depth} blocks the dim {d} does not take red first");
        }

        var mid = DeepWaterFog.Dim(WorldGenerationConfig.DeepWaterFogScaleBlocks);
        Assert.True(mid.X > mid.Z, $"red {mid.X} is not dimmed more than blue {mid.Z}");
    }

    [Fact]
    public void TheDeepestWaterIsNeverPitchBlack()
    {
        for (var depth = 0f; depth <= 10000f; depth += 7.5f)
        {
            var t = DeepWaterFog.Tint(depth);
            Assert.True(t.X > 0f && t.Y > 0f && t.Z > 0f, $"the fog went black at {depth} blocks ({t})");
            Assert.True(t.X >= 1f - WorldGenerationConfig.DeepWaterFogMaxDim - 1e-6f);
        }
    }
}
