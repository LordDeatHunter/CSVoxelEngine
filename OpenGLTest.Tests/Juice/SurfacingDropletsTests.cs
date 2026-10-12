using VoxelEngine.Juice;
using Xunit;

namespace VoxelEngine.Tests.Juice;

public sealed class SurfacingDropletsTests
{
    private const float Dt = 1f / 60f;

    // One frame as the camera runs it: aged first, then told the frame's final eye state
    private static void Frame(SurfacingDroplets droplets, bool eyeInFluid, bool eyeInLava = false)
    {
        droplets.Update(Dt);
        droplets.Observe(eyeInFluid, eyeInLava);
    }

    private static void Frames(SurfacingDroplets droplets, int count, bool eyeInFluid, bool eyeInLava = false)
    {
        for (var i = 0; i < count; i++)
            Frame(droplets, eyeInFluid, eyeInLava);
    }

    [Fact]
    public void OnlyTheEyeComingUpOutOfWaterStartsThem()
    {
        var droplets = new SurfacingDroplets();
        // Dry from the start, then diving in, and staying under: nothing on the screen
        Frames(droplets, 30, eyeInFluid: false);
        Assert.Equal(0f, droplets.Intensity);
        Frames(droplets, 30, eyeInFluid: true);
        Assert.Equal(0f, droplets.Intensity);

        Frame(droplets, eyeInFluid: false);
        Assert.Equal(1f, droplets.Intensity);
        Assert.Equal(0f, droplets.Seconds);
    }

    [Fact]
    public void SurfacingFromLavaShowsNone()
    {
        var droplets = new SurfacingDroplets();
        Frames(droplets, 30, eyeInFluid: true, eyeInLava: true);
        Frame(droplets, eyeInFluid: false);
        Assert.Equal(0f, droplets.Intensity);
    }

    [Fact]
    public void TheySlideAndFadeOutAndTheNextSurfacingScattersANewPattern()
    {
        var droplets = new SurfacingDroplets();
        Frames(droplets, 10, eyeInFluid: true);
        Frame(droplets, eyeInFluid: false);
        var firstSeed = droplets.Seed;

        var previous = droplets.Intensity;
        var previousSeconds = droplets.Seconds;
        for (var t = 0f; t < WorldGenerationConfig.SurfacingDropletSeconds + 0.5f; t += Dt)
        {
            Frame(droplets, eyeInFluid: false);
            Assert.True(droplets.Intensity <= previous);
            Assert.True(droplets.Seconds > previousSeconds);
            previous = droplets.Intensity;
            previousSeconds = droplets.Seconds;
        }
        Assert.Equal(0f, droplets.Intensity);

        Frames(droplets, 10, eyeInFluid: true);
        Frame(droplets, eyeInFluid: false);
        Assert.Equal(1f, droplets.Intensity);
        Assert.NotEqual(firstSeed, droplets.Seed);
    }

    [Fact]
    public void GoingBackUnderClearsThemAtOnce()
    {
        var droplets = new SurfacingDroplets();
        Frames(droplets, 10, eyeInFluid: true);
        Frames(droplets, 10, eyeInFluid: false);
        Assert.True(droplets.Intensity > 0f);

        Frame(droplets, eyeInFluid: true);
        Assert.Equal(0f, droplets.Intensity);
        Assert.Equal(0f, droplets.Seconds);
    }
}
