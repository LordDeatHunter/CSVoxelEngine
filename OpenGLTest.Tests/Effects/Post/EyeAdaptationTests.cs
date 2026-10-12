using System;
using VoxelEngine.Effects.Post;
using Xunit;

namespace VoxelEngine.Tests.Effects.Post;

/// <summary>
/// The eye adaptation (<see cref="EyeAdaptation"/>): the exposure rises only in the dark, never under
/// open sky or in a lit room, and the eye takes longer to get used to the dark than to the light.
/// </summary>
public sealed class EyeAdaptationTests
{
    [Fact]
    public void UnderOpenSkyTheExposureIsLeftAlone()
    {
        // Sky light is full outdoors at any hour, so the night is not brightened either
        foreach (var block in new[] { 0f, 0.4f, 1f })
            Assert.Equal(1f, EyeAdaptation.Target(1f, block));
    }

    [Fact]
    public void ALitRoomIsNeverBrightened()
    {
        for (var block = WorldGenerationConfig.EyeAdaptFullLight; block <= 1f; block += 0.01f)
            Assert.Equal(1f, EyeAdaptation.Target(0f, block));
    }

    [Fact]
    public void DarknessRaisesTheExposureToItsCapAndMoreLightNeverRaisesIt()
    {
        Assert.Equal(WorldGenerationConfig.EyeAdaptMaxExposure, EyeAdaptation.Target(0f, 0f), 5);
        var previous = float.MaxValue;
        for (var light = 0f; light <= 1f; light += 0.01f)
        {
            var sky = EyeAdaptation.Target(light, 0f);
            var lamp = EyeAdaptation.Target(0f, light);
            Assert.Equal(sky, lamp);
            Assert.InRange(sky, 1f, WorldGenerationConfig.EyeAdaptMaxExposure);
            Assert.True(sky <= previous, $"more light raised the exposure ({light})");
            previous = sky;
        }
    }

    [Fact]
    public void TheEyeAdaptsToTheDarkMoreSlowlyThanBackToTheLight()
    {
        var max = WorldGenerationConfig.EyeAdaptMaxExposure;
        var half = (1f + max) / 2f;
        Assert.True(SecondsToCross(1f, max, half) > SecondsToCross(max, 1f, half));
    }

    [Fact]
    public void TheExposureEasesWithoutOvershooting()
    {
        var max = WorldGenerationConfig.EyeAdaptMaxExposure;
        var exposure = 1f;
        for (var i = 0; i < 2000; i++)
        {
            var next = EyeAdaptation.Step(exposure, max, 1f / 60f);
            Assert.True(next >= exposure && next <= max);
            exposure = next;
        }

        for (var i = 0; i < 2000; i++)
        {
            var next = EyeAdaptation.Step(exposure, 1f, 1f / 60f);
            Assert.True(next <= exposure && next >= 1f);
            exposure = next;
        }
    }

    private static float SecondsToCross(float from, float to, float mark)
    {
        var exposure = from;
        var seconds = 0f;
        while (MathF.Sign(mark - exposure) == MathF.Sign(mark - from) && seconds < 120f)
        {
            exposure = EyeAdaptation.Step(exposure, to, 1f / 60f);
            seconds += 1f / 60f;
        }

        return seconds;
    }
}
