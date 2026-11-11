using System;
using System.Numerics;
using VoxelEngine.Effects.Surfaces;
using Xunit;

namespace VoxelEngine.Tests.Effects.Surfaces;

/// <summary>
/// The battle tower dome's forcefield film (<see cref="ForcefieldShimmer"/>, the twin of the forcefield term in
/// test-fragment.glsl): barely visible, brighter toward a graze, a honeycomb that repeats, a shimmer that moves, and nothing
/// left to alias far away.
/// </summary>
public sealed class ForcefieldShimmerTests
{
    private const float Strength = SurfaceShadingConfig.ForcefieldStrength;
    private const float Near = 4f;
    private static readonly float Far = SurfaceShadingConfig.ForcefieldDetailFadeFarBlocks + 1f;

    // A spread of face points, heights and times
    private static (Vector2 Face, Vector3 World, float Time)[] Samples()
    {
        var samples = new (Vector2, Vector3, float)[64];
        var random = new Random(11);
        for (var i = 0; i < samples.Length; i++)
        {
            var world = new Vector3(random.NextSingle() * 400f - 200f, 40f + random.NextSingle() * 30f, random.NextSingle() * 400f - 200f);
            samples[i] = (new Vector2(world.X, world.Y), world, random.NextSingle() * 600f);
        }
        return samples;
    }

    [Fact]
    public void TheFilmIsFaintestHeadOnAndBrightensTowardAGrazeFromEitherSide()
    {
        foreach (var (face, world, time) in Samples())
        {
            var previous = ForcefieldShimmer.Alpha(face, world, 1f, time, Near, Strength);
            for (var cos = 1f; cos >= 0f; cos -= 0.02f)
            {
                var alpha = ForcefieldShimmer.Alpha(face, world, cos, time, Near, Strength);
                Assert.True(alpha >= previous - 1e-6f, $"the film faded from {previous} to {alpha} toward a graze ({cos})");
                Assert.Equal(alpha, ForcefieldShimmer.Alpha(face, world, -cos, time, Near, Strength), 6);
                previous = alpha;
            }
            Assert.True(ForcefieldShimmer.Alpha(face, world, 0f, time, Near, Strength)
                        > ForcefieldShimmer.Alpha(face, world, 1f, time, Near, Strength));
        }
    }

    [Fact]
    public void TheBackgroundAlwaysShowsThroughMoreThanTheFilm()
    {
        foreach (var (face, world, time) in Samples())
        foreach (var cos in new[] { 1f, 0.6f, 0.2f, 0f })
        foreach (var distance in new[] { 0f, Near, 30f, Far })
            Assert.InRange(ForcefieldShimmer.Alpha(face, world, cos, time, distance, Strength), 0f, 0.5f - 1e-3f);
    }

    [Fact]
    public void NoStrengthLeavesNoFilm()
    {
        foreach (var (face, world, time) in Samples())
            Assert.Equal(0f, ForcefieldShimmer.Alpha(face, world, 0.3f, time, Near, 0f));
    }

    [Fact]
    public void TheHoneycombsEdgesOutshineItsCentersAndItRepeats()
    {
        // Cell centers sit on a triangular lattice one unit apart; an edge lies halfway between two neighbors
        Assert.Equal(0f, ForcefieldShimmer.HexEdge(Vector2.Zero), 5);
        Assert.Equal(0f, ForcefieldShimmer.HexEdge(new Vector2(0.5f, 0.8660254f)), 4);
        Assert.Equal(1f, ForcefieldShimmer.HexEdge(new Vector2(0.5f, 0f)), 4);
        Assert.Equal(1f, ForcefieldShimmer.HexEdge(new Vector2(0.25f, 0.4330127f)), 4);

        var random = new Random(3);
        for (var i = 0; i < 200; i++)
        {
            var p = new Vector2(random.NextSingle() * 40f - 20f, random.NextSingle() * 40f - 20f);
            var edge = ForcefieldShimmer.HexEdge(p);
            Assert.InRange(edge, 0f, 1f);
            Assert.Equal(edge, ForcefieldShimmer.HexEdge(p + new Vector2(1f, 0f)), 3);
            Assert.Equal(edge, ForcefieldShimmer.HexEdge(p + new Vector2(0.5f, 0.8660254f)), 3);
        }
    }

    [Fact]
    public void TheShimmerMovesOverTime()
    {
        foreach (var (face, world, _) in Samples())
        {
            float min = float.MaxValue, max = float.MinValue;
            for (var t = 0f; t < 6f; t += 0.1f)
            {
                var alpha = ForcefieldShimmer.Alpha(face, world, 0.8f, t, Near, Strength);
                min = MathF.Min(min, alpha);
                max = MathF.Max(max, alpha);
            }
            Assert.True(max - min > 1e-3f, $"the film at {world} stood still");
        }
    }

    [Fact]
    public void FarAwayOnlyTheSmoothFilmIsLeft()
    {
        // Past the detail fade the honeycomb and the band are gone, so the film no longer depends on where on a face or when
        // it is seen, and nothing turns into subpixel sparkle
        foreach (var cos in new[] { 1f, 0.5f, 0.1f })
        {
            var reference = ForcefieldShimmer.Alpha(Vector2.Zero, new Vector3(0f, 50f, 0f), cos, 0f, Far, Strength);
            foreach (var (face, world, time) in Samples())
                Assert.Equal(reference, ForcefieldShimmer.Alpha(face, world, cos, time, Far, Strength), 5);
        }
    }
}
