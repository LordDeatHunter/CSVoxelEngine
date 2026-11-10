using System.Numerics;
using VoxelEngine.Effects.Post;
using Xunit;

namespace VoxelEngine.Tests.Effects.Post;

/// <summary>
/// The lens flare (<see cref="LensFlare"/>, the twin of the circles dazzle-fragment.glsl draws):
/// only with the sun in view but off the middle of the screen, only while its disc shows and never
/// under water; every circle lies on the line from the sun through the screen center and ends at
/// its own edge.
/// </summary>
public sealed class LensFlareTests
{
    private static float At(float degrees) => LensFlare.Intensity(MathUtils.CosDeg(degrees), 1f, 0f);

    [Fact]
    public void ThereIsNoFlareLookingStraightAtTheSunNorWithItWellOffToTheSide()
    {
        Assert.Equal(0f, At(0f));
        Assert.Equal(0f, At(PostProcessConfig.LensFlareStartDegrees));
        Assert.Equal(0f, At(PostProcessConfig.LensFlareGoneDegrees));
        Assert.Equal(0f, At(90f));
        Assert.Equal(0f, At(180f));
    }

    [Fact]
    public void TheFlareRisesAsTheSunLeavesTheMiddleHoldsAndThenFadesWithoutEverPassingItsCap()
    {
        const float cap = 1f;
        Assert.Equal(cap, At(PostProcessConfig.LensFlareFullDegrees), 5);
        Assert.Equal(cap, At(PostProcessConfig.LensFlareFadeDegrees), 5);
        var previous = 0f;
        for (var degrees = 0f; degrees <= PostProcessConfig.LensFlareFullDegrees; degrees += 0.1f)
        {
            Assert.True(At(degrees) >= previous - 1e-5f, $"the flare fell as the sun left the middle ({degrees})");
            previous = At(degrees);
        }

        previous = cap;
        for (var degrees = PostProcessConfig.LensFlareFadeDegrees; degrees <= 180f; degrees += 0.25f)
        {
            Assert.True(At(degrees) <= previous + 1e-5f, $"the flare rose again turning off the sun ({degrees})");
            previous = At(degrees);
        }

        for (var degrees = 0f; degrees <= 180f; degrees += 0.5f)
            Assert.InRange(At(degrees), 0f, cap + 1e-5f);
    }

    [Fact]
    public void TheFlareGoesWithTheSunsDiscAndUnderWater()
    {
        var alignment = MathUtils.CosDeg(PostProcessConfig.LensFlareFullDegrees);
        Assert.Equal(0f, LensFlare.Intensity(alignment, 0f, 0f));
        Assert.Equal(0f, LensFlare.Intensity(alignment, 1f, 1f));
        var previousDisc = 0f;
        var previousWet = LensFlare.Intensity(alignment, 1f, 0f);
        for (var t = 0f; t <= 1f; t += 0.05f)
        {
            var disc = LensFlare.Intensity(alignment, t, 0f);
            var wet = LensFlare.Intensity(alignment, 1f, t);
            Assert.True(disc >= previousDisc, "the flare fell as the disc faded in");
            Assert.True(wet <= previousWet, "the flare grew as the eye went under");
            previousDisc = disc;
            previousWet = wet;
        }
    }

    [Fact]
    public void ACircleCoversThePictureOnlyAsFarAsTheFlareAndTheSunAreBright()
    {
        var day = new Vector3(1f, 0.9f, 0.75f);
        Assert.Equal(0f, LensFlare.Cover(0f, day));
        Assert.Equal(0f, LensFlare.Cover(1f, Vector3.Zero));
        Assert.Equal(PostProcessConfig.LensFlareCover, LensFlare.Cover(1f, day), 5);
        // An over-bright sun covers no more than a bright one; a dim one covers less, in step with
        // the light its circles add, so they never turn into dark smudges
        Assert.Equal(LensFlare.Cover(1f, day), LensFlare.Cover(1f, day * 3f), 5);
        Assert.Equal(0.25f * LensFlare.Cover(1f, day), LensFlare.Cover(1f, day * 0.25f), 5);
        Assert.Equal(0.5f * LensFlare.Cover(1f, day), LensFlare.Cover(0.5f, day), 5);
        // The brightest configured circle still lets most of the picture through
        foreach (var ghost in PostProcessConfig.LensFlareGhosts)
            Assert.True(ghost.Brightness * LensFlare.Cover(1f, day) < 0.5f, "a circle hid most of the picture behind it");
    }

    [Fact]
    public void TheCirclesLieOnTheLineFromTheSunThroughTheScreenCenter()
    {
        var sun = new Vector2(0.4f, 0.25f);
        Assert.Equal(sun, LensFlare.Center(sun, 1f));
        Assert.Equal(Vector2.Zero, LensFlare.Center(sun, 0f));
        Assert.Equal(-sun, LensFlare.Center(sun, -1f));
        // With the sun in the middle of the screen every circle sits on it
        foreach (var ghost in PostProcessConfig.LensFlareGhosts)
            Assert.Equal(Vector2.Zero, LensFlare.Center(Vector2.Zero, ghost.Offset));
    }

    [Fact]
    public void ACircleEndsAtItsEdgeAndAHollowOneIsDarkInTheMiddle()
    {
        foreach (var softness in new[] { 0.1f, 0.3f, 0.6f })
        foreach (var hollow in new[] { 0f, 0.5f, 1f })
        {
            Assert.Equal(0f, LensFlare.Shape(1f, hollow, softness));
            Assert.Equal(0f, LensFlare.Shape(1.5f, hollow, softness));
            for (var d = 0f; d <= 1f; d += 0.01f)
                Assert.InRange(LensFlare.Shape(d, hollow, softness), 0f, 1f);
        }

        // An even disc is fully lit up to where its edge starts to fade; a ring has nothing in
        // the middle and its light out by the rim
        Assert.Equal(1f, LensFlare.Shape(0f, 0f, 0.3f));
        Assert.Equal(1f, LensFlare.Shape(0.7f, 0f, 0.3f), 5);
        Assert.Equal(0f, LensFlare.Shape(0f, 1f, 0.3f));
        Assert.True(LensFlare.Shape(0.8f, 1f, 0.3f) > LensFlare.Shape(0.3f, 1f, 0.3f));
    }

    [Fact]
    public void TheConfiguredCirclesFitTheShaderAndAreDrawable()
    {
        var ghosts = PostProcessConfig.LensFlareGhosts;
        Assert.InRange(ghosts.Length, 1, LensFlare.MaxGhosts);
        Assert.Equal(ghosts.Length, LensFlare.PackShapes().Length);
        Assert.Equal(ghosts.Length, LensFlare.PackColors().Length);
        foreach (var ghost in ghosts)
        {
            Assert.True(ghost.Radius > 0f, "a circle without a radius divides by zero in the shader");
            Assert.InRange(ghost.Hollow, 0f, 1f);
            Assert.True(ghost.Softness > 0f && ghost.Softness <= 1f, "a circle's edge fades over part of its own radius");
            Assert.True(ghost.Brightness > 0f);
        }
    }
}
