using System.Numerics;
using VoxelEngine.Rendering;
using Xunit;
using static VoxelEngine.Rendering.FarShadowReuse;

namespace VoxelEngine.Tests.Rendering;

/// <summary>
/// <see cref="FarShadowReuse.Decide"/> is the gate that lets the far shadow map be rendered once and
/// sampled for many frames. A still frame must reuse it, and each way the cached map can go stale
/// (a re-center, eye depth along the light, a key-light turn, the moonlight handover, a caster geometry
/// change, plain age) must refresh it. Every magnitude is half or one and a half times its threshold,
/// derived from <c>LightingConfig</c>, so the facts survive retuning; one trigger per test.
/// </summary>
public sealed class FarShadowReuseTests
{
    private const float Radius = LightingConfig.FarShadowRadiusBlocks;
    private const int MapSize = LightingConfig.FarShadowMapSize;
    private const float Inner = LightingConfig.FarShadowReuseInnerFraction * Radius;
    private const float Margin = LightingConfig.FarShadowReuseDepthMarginFraction * Radius;

    private static readonly Vector3 Eye = new(40.5f, 70f, -12.25f);
    private static readonly Vector3 Target = new(130.5f, 52f, 20.75f);
    private const int Version = 7;

    /// <summary>The key-light turn bound in radians: the allowed texels at the box edge.</summary>
    private static float MaxTurn =>
        LightingConfig.FarShadowReuseMaxTurnTexels * (2f * Radius / MapSize) / Radius;

    private static Pose Cached(Vector3 sun, bool moonLit = false) => new(Target, Eye, sun, moonLit, Version);

    private static Frame Still(Vector3 sun, bool moonLit = false) => new(Target, Eye, sun, moonLit, Version);

    private static Refresh Decide(Vector3 sun, in Frame frame, int age = 1, bool hasMap = true) =>
        FarShadowReuse.Decide(Cached(sun), frame, hasMap, age, Radius, MapSize);

    [Fact]
    public void NothingCachedAlwaysRenders()
    {
        foreach (var sun in ShadowCenterTests.Suns)
        foreach (var age in new[] { 0, 1, LightingConfig.FarShadowReuseMaxFrames + 5 })
            Assert.Equal(Refresh.NoMap, Decide(sun, Still(sun), age, hasMap: false));
    }

    [Fact]
    public void AStillFrameReusesTheMap()
    {
        foreach (var sun in ShadowCenterTests.Suns)
        {
            Assert.Equal(Refresh.None, Decide(sun, Still(sun)));
            Assert.Equal(Refresh.None,
                FarShadowReuse.Decide(Cached(sun, moonLit: true), Still(sun, moonLit: true), true, 1, Radius, MapSize));
        }
    }

    [Fact]
    public void TheTargetLeavingTheInnerRegionRecenters()
    {
        foreach (var sun in ShadowCenterTests.Suns)
        {
            var (right, up) = ShadowCascade.LightAxes(sun);
            foreach (var axis in new[] { right, -right, up, -up })
            {
                Assert.Equal(Refresh.Recenter, Decide(sun, Still(sun) with { Target = Target + axis * (1.5f * Inner) }));
                Assert.Equal(Refresh.None, Decide(sun, Still(sun) with { Target = Target + axis * (0.5f * Inner) }));
            }
        }
    }

    [Fact]
    public void MovingTheTargetAlongTheKeyLightAloneNeverRecenters()
    {
        foreach (var sun in ShadowCenterTests.Suns)
        foreach (var distance in new[] { -1.5f * Inner, 1.5f * Inner, 3f * Radius })
            Assert.Equal(Refresh.None, Decide(sun, Still(sun) with { Target = Target + sun * distance }));
    }

    [Fact]
    public void TheEyeMovingAlongTheKeyLightPastTheMarginRefreshes()
    {
        foreach (var sun in ShadowCenterTests.Suns)
        {
            foreach (var sign in new[] { -1f, 1f })
            {
                Assert.Equal(Refresh.Depth, Decide(sun, Still(sun) with { Eye = Eye + sun * (sign * 1.5f * Margin) }));
                Assert.Equal(Refresh.None, Decide(sun, Still(sun) with { Eye = Eye + sun * (sign * 0.5f * Margin) }));
            }

            // Sideways eye motion is the target's business (the re-center), not depth's.
            var (right, up) = ShadowCascade.LightAxes(sun);
            foreach (var axis in new[] { right, -right, up, -up })
                Assert.Equal(Refresh.None, Decide(sun, Still(sun) with { Eye = Eye + axis * (1.5f * Margin) }));
        }
    }

    [Fact]
    public void AKeyLightTurnPastTheTexelBoundRefreshesAndASmallerOneDoesNot()
    {
        foreach (var sun in ShadowCenterTests.Suns)
        {
            var (right, up) = ShadowCascade.LightAxes(sun);
            foreach (var axis in new[] { right, up })
            {
                var big = Vector3.Transform(sun, Quaternion.CreateFromAxisAngle(axis, 1.5f * MaxTurn));
                var small = Vector3.Transform(sun, Quaternion.CreateFromAxisAngle(axis, 0.5f * MaxTurn));
                Assert.Equal(Refresh.KeyLightTurn, Decide(sun, Still(sun) with { KeyLight = big }));
                Assert.Equal(Refresh.None, Decide(sun, Still(sun) with { KeyLight = small }));
            }
        }
    }

    [Fact]
    public void TheMoonlightHandoverRefreshesEvenThoughTheKeyLightIsAntiParallel()
    {
        foreach (var sun in ShadowCenterTests.Suns)
        {
            var handover = Still(sun) with { KeyLight = -sun, MoonLit = true };
            Assert.Equal(Refresh.Handover, Decide(sun, handover));
            Assert.NotEqual(Refresh.KeyLightTurn, Decide(sun, handover));
        }
    }

    [Fact]
    public void AGeometryChangeRefreshesOnlyOnceTheMinimumAgeHasPassed()
    {
        var sun = ShadowCenterTests.SunAt(1.2f);
        var changed = Still(sun) with { GeometryVersion = Version + 1 };
        for (var age = 1; age < LightingConfig.FarShadowReuseMaxFrames; age++)
        {
            Assert.Equal(age >= LightingConfig.FarShadowReuseGeometryMinFrames,
                Decide(sun, changed, age) == Refresh.Geometry);
            Assert.Equal(Refresh.None, Decide(sun, Still(sun), age));
        }
    }

    [Fact]
    public void TheMapIsNeverReusedForMaxFramesInARow()
    {
        var sun = ShadowCenterTests.SunAt(2.4f);
        var age = 0;
        var run = 0;
        int reuses = 0, renders = 0;

        for (var frame = 0; frame < 10 * LightingConfig.FarShadowReuseMaxFrames; frame++)
        {
            age++;
            if (Decide(sun, Still(sun), age) == Refresh.None)
            {
                reuses++;
                run++;
                Assert.True(run < LightingConfig.FarShadowReuseMaxFrames, $"reused {run} frames in a row");
                continue;
            }

            renders++;
            age = 0;
            run = 0;
        }

        Assert.True(reuses > 0, "a still camera never reused the far map");
        Assert.True(renders > 0, "a still camera never re-rendered the far map");
    }

    [Fact]
    public void ADegenerateKeyLightRefreshes()
    {
        var sun = ShadowCenterTests.SunAt(0.4f);
        Assert.Equal(Refresh.KeyLightTurn, Decide(sun, Still(sun) with { KeyLight = Vector3.Zero }));
        Assert.Equal(Refresh.KeyLightTurn,
            FarShadowReuse.Decide(Cached(Vector3.Zero), Still(sun), true, 1, Radius, MapSize));
    }
}
