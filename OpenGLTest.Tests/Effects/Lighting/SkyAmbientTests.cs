using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using VoxelEngine.Core;
using VoxelEngine.Effects.Lighting;
using Xunit;

namespace VoxelEngine.Tests.Effects.Lighting;

/// <summary>Ambient is skylight bounce: a sealed room is lit by local light alone, whatever the clock says.</summary>
public sealed class SkyAmbientTests
{
    private static readonly Vector3 Night = new(0.01f, 0.012f, 0.03f);
    private static readonly Vector3 Day = new(0.15f, 0.17f, 0.23f);
    private static readonly Vector3 Torch = new(0.4f, 0.25f, 0.1f);

    [Theory]
    [InlineData(-1f)]
    [InlineData(0f)]
    public void WithNoSkylightTheColorIsLocalLightOnlyAndIndependentOfTheDayPalette(float sun)
    {
        foreach (var ny in new[] { -1f, 0f, 1f })
        {
            var night = SkyAmbient.Compose(Night, Night * 0.5f, ny, sun, Vector3.Zero, Torch);
            var day = SkyAmbient.Compose(Day, Day * 0.5f, ny, sun, Vector3.Zero, Torch);
            Assert.Equal(Torch, night);
            Assert.Equal(night, day);
            Assert.Equal(Vector3.Zero, SkyAmbient.Compose(Day, Day, ny, sun, Vector3.Zero, Vector3.Zero));
        }
    }

    [Fact]
    public void WithSkylightTheAmbientStillFollowsTheClockAndTheLocalLightStacksUnchanged()
    {
        var night = SkyAmbient.Compose(Night, Night, 1f, 1f, Vector3.Zero, Vector3.Zero);
        var day = SkyAmbient.Compose(Day, Day, 1f, 1f, Vector3.Zero, Vector3.Zero);
        Assert.True(day.X > night.X);
        var half = SkyAmbient.Compose(Day, Day, 1f, 0.5f, Vector3.Zero, Vector3.Zero);
        Assert.True(half.X > 0f && half.X < day.X);
        Assert.Equal(Vector3.Max(Torch, new Vector3(0.1f)),
            SkyAmbient.Compose(Vector3.Zero, Vector3.Zero, 0f, 1f, new Vector3(0.1f), Torch));
    }

    [Fact]
    public void NoShaderKeepsAnAmbientFloorUnderTheSkylightGate()
    {
        var dir = Path.Combine(AssetRoot.Find(), "shaders");
        foreach (var file in new[] { "test-fragment.glsl", "leaf-particle-fragment.glsl" })
        {
            var text = File.ReadAllText(Path.Combine(dir, file));
            Assert.DoesNotMatch(new Regex(@"0\.12\s*\+\s*0\.88\s*\*\s*fSun"), text);
        }
        Assert.Contains("float SkyGate(float sun) { return clamp(sun, 0.0, 1.0); }",
            File.ReadAllText(Path.Combine(dir, "test-fragment.glsl")));
    }
}
