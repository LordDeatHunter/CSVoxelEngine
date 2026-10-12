using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <c>ReflectionReuse</c> is the GL-free gate in front of the planar water mirror: it decides
/// whether a frame re-renders the reflection or reuses the cached texture and the view-projection
/// that produced it. The GL half of that pass can only be verified by eye and by the perf-fly
/// protocol in PERF_TODO.md, so every <em>decision</em> the pass makes is pinned here instead —
/// no <c>Camera</c>, no <c>ChunkManager</c>, no window and no GL anywhere in this file. The
/// eye-side gate (<c>MirrorApplies</c>: no mirror below the plane or with the eye in water) is
/// pinned here too.
///
/// Every magnitude below is expressed as a multiple of the <c>WorldGenerationConfig</c> threshold
/// it exercises, so retuning a constant cannot silently turn a test into a tautology.
/// </summary>
public sealed class ReflectionReuseTests
{
    private static ReflectionReuse.ReflectionPose Pose(
        Vector3 eye,
        Vector3 fwd,
        float fov = 70f,
        float aspect = 1.6f,
        float planeY = 0.875f) => new(eye, fwd, fov, aspect, planeY);

    private static Vector3 Yawed(float degrees)
    {
        var rad = degrees * MathF.PI / 180f;
        return new Vector3(MathF.Cos(rad), 0f, MathF.Sin(rad));
    }

    [Fact]
    public void StillCameraReusesTheMirror()
    {
        var pose = Pose(new Vector3(10f, 5f, -3f), Yawed(0f));

        Assert.False(ReflectionReuse.ShouldRefresh(pose, pose,
            hasCachedMirror: true, targetRecreated: false, reusedLastFrame: false));
    }

    [Fact]
    public void FirstMirrorAlwaysRenders()
    {
        var pose = Pose(new Vector3(10f, 5f, -3f), Yawed(0f));

        // Identical poses, but there is no texture to reuse yet.
        Assert.True(ReflectionReuse.ShouldRefresh(pose, pose,
            hasCachedMirror: false, targetRecreated: false, reusedLastFrame: false));
    }

    [Fact]
    public void RecreatedTargetAlwaysRenders()
    {
        // The recreated color texture is TexImage2D'd with null data, so its contents are
        // undefined and nothing may be sampled out of it until a real mirror render lands.
        // This Fact pins the pure function only: whether the caller ever passes true is a
        // RenderManager concern (the invalidation inside CreateReflectionTarget) and is NOT
        // covered by any test here.
        var pose = Pose(new Vector3(10f, 5f, -3f), Yawed(0f));

        Assert.True(ReflectionReuse.ShouldRefresh(pose, pose,
            hasCachedMirror: true, targetRecreated: true, reusedLastFrame: false));
    }

    [Fact]
    public void NeverSkipsTwoFramesInARow()
    {
        // The mirror bakes in the shadow map, sun and clouds of the frame that drew it; capping
        // reuse at one frame keeps that shading from stepping at fps/2 for longer than a frame.
        var pose = Pose(new Vector3(10f, 5f, -3f), Yawed(0f));

        Assert.True(ReflectionReuse.ShouldRefresh(pose, pose,
            hasCachedMirror: true, targetRecreated: false, reusedLastFrame: true));
    }

    [Fact]
    public void SmallDriftStillReuses()
    {
        var eye = new Vector3(10f, 5f, -3f);
        var cached = Pose(eye, Yawed(0f));
        var current = Pose(
            eye + new Vector3(0.5f * WorldGenerationConfig.ReflectionReuseMaxMoveBlocks, 0f, 0f),
            Yawed(0.5f * WorldGenerationConfig.ReflectionReuseMaxDegrees));

        Assert.False(ReflectionReuse.ShouldRefresh(cached, current,
            hasCachedMirror: true, targetRecreated: false, reusedLastFrame: false));
    }

    [Fact]
    public void HardTurnForcesRefresh()
    {
        var eye = new Vector3(10f, 5f, -3f);
        var cached = Pose(eye, Yawed(0f));
        var current = Pose(eye, Yawed(3f * WorldGenerationConfig.ReflectionReuseMaxDegrees));

        Assert.True(ReflectionReuse.ShouldRefresh(cached, current,
            hasCachedMirror: true, targetRecreated: false, reusedLastFrame: false));
    }

    [Fact]
    public void FastMoveForcesRefresh()
    {
        var eye = new Vector3(10f, 5f, -3f);
        var cached = Pose(eye, Yawed(0f));
        var current = Pose(
            eye + new Vector3(0f, 0f, 2f * WorldGenerationConfig.ReflectionReuseMaxMoveBlocks),
            Yawed(0f));

        Assert.True(ReflectionReuse.ShouldRefresh(cached, current,
            hasCachedMirror: true, targetRecreated: false, reusedLastFrame: false));
    }

    [Fact]
    public void SprintFovEaseForcesRefresh()
    {
        var eye = new Vector3(10f, 5f, -3f);
        var cached = Pose(eye, Yawed(0f));

        var eased = Pose(eye, Yawed(0f),
            fov: 70f + 2f * WorldGenerationConfig.ReflectionReuseMaxFovDegrees);
        Assert.True(ReflectionReuse.ShouldRefresh(cached, eased,
            hasCachedMirror: true, targetRecreated: false, reusedLastFrame: false));

        // The tail of the same ease, once the damp has nearly converged, reuses again.
        var settled = Pose(eye, Yawed(0f),
            fov: 70f + 0.25f * WorldGenerationConfig.ReflectionReuseMaxFovDegrees);
        Assert.False(ReflectionReuse.ShouldRefresh(cached, settled,
            hasCachedMirror: true, targetRecreated: false, reusedLastFrame: false));
    }

    [Fact]
    public void AspectChangeForcesRefresh()
    {
        // A resize that rounds to the same reflection texture size still changes the projection
        // baked into the cached view-projection, so the aspect is checked in its own right.
        var eye = new Vector3(10f, 5f, -3f);
        var cached = Pose(eye, Yawed(0f));
        var current = Pose(eye, Yawed(0f),
            aspect: 1.6f + 10f * WorldGenerationConfig.ReflectionReuseMaxAspect);

        Assert.True(ReflectionReuse.ShouldRefresh(cached, current,
            hasCachedMirror: true, targetRecreated: false, reusedLastFrame: false));
    }

    [Fact]
    public void WaterPlaneChangeForcesRefresh()
    {
        var eye = new Vector3(10f, 5f, -3f);
        var cached = Pose(eye, Yawed(0f));
        var current = Pose(eye, Yawed(0f), planeY: 0.875f + 1f);

        Assert.True(ReflectionReuse.ShouldRefresh(cached, current,
            hasCachedMirror: true, targetRecreated: false, reusedLastFrame: false));
    }

    [Theory]
    [InlineData(+5f, false, true)]
    // The pond-above-sea-level case: the eye is submerged yet numerically above the sea plane.
    [InlineData(+5f, true, false)]
    [InlineData(-5f, false, false)]
    public void MirrorOnlyAppliesToADryEyeAboveThePlane(float eyeOffset, bool eyeInWater, bool expected)
    {
        Assert.Equal(expected, ReflectionReuse.MirrorApplies(0.875f + eyeOffset, 0.875f, eyeInWater));
    }

    private static readonly Vector3[] Entities =
        [new(1f, 2f, 3f), new(-4f, 1.5f, 8f), new(12f, 0.9f, -6f)];

    private static Vector3[] Shifted(Vector3[] src, int index, Vector3 delta)
    {
        var copy = (Vector3[])src.Clone();
        copy[index] += delta;
        return copy;
    }

    [Fact]
    public void StillEntitiesDoNotForceRefresh()
    {
        Assert.False(ReflectionReuse.EntitiesMoved(Entities, (Vector3[])Entities.Clone(),
            WorldGenerationConfig.ReflectionReuseMaxEntityMoveBlocks));
    }

    [Fact]
    public void SubThresholdEntityDriftStillReuses()
    {
        var current = Shifted(Entities, 1,
            new Vector3(0f, 0.5f * WorldGenerationConfig.ReflectionReuseMaxEntityMoveBlocks, 0f));

        Assert.False(ReflectionReuse.EntitiesMoved(Entities, current,
            WorldGenerationConfig.ReflectionReuseMaxEntityMoveBlocks));
    }

    [Fact]
    public void MovingEntityForcesRefresh()
    {
        var current = Shifted(Entities, 2,
            new Vector3(2f * WorldGenerationConfig.ReflectionReuseMaxEntityMoveBlocks, 0f, 0f));

        Assert.True(ReflectionReuse.EntitiesMoved(Entities, current,
            WorldGenerationConfig.ReflectionReuseMaxEntityMoveBlocks));
    }

    [Fact]
    public void EntityCountChangeForcesRefresh()
    {
        // A drop picked up (or a slime spawned) changes the mirror even if nothing else moved.
        Assert.True(ReflectionReuse.EntitiesMoved(Entities, Entities.AsSpan(0, Entities.Length - 1),
            WorldGenerationConfig.ReflectionReuseMaxEntityMoveBlocks));
        Assert.True(ReflectionReuse.EntitiesMoved(ReadOnlySpan<Vector3>.Empty, Entities,
            WorldGenerationConfig.ReflectionReuseMaxEntityMoveBlocks));
    }

    [Fact]
    public void EntityMotionOverridesAReusablePose()
    {
        var pose = Pose(new Vector3(10f, 5f, -3f), Yawed(0f));

        Assert.True(ReflectionReuse.ShouldRefresh(pose, pose,
            hasCachedMirror: true, targetRecreated: false, reusedLastFrame: false, entitiesMoved: true));
        Assert.False(ReflectionReuse.ShouldRefresh(pose, pose,
            hasCachedMirror: true, targetRecreated: false, reusedLastFrame: false, entitiesMoved: false));
    }

    [Fact]
    public void DegenerateForwardForcesRefresh()
    {
        var eye = new Vector3(10f, 5f, -3f);
        var cached = Pose(eye, Yawed(0f));
        var current = Pose(eye, Vector3.Zero);

        Assert.True(ReflectionReuse.ShouldRefresh(cached, current,
            hasCachedMirror: true, targetRecreated: false, reusedLastFrame: false));
        Assert.True(ReflectionReuse.ShouldRefresh(Pose(eye, Vector3.Zero), cached,
            hasCachedMirror: true, targetRecreated: false, reusedLastFrame: false));
    }
}
