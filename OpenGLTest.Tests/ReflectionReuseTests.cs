using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <c>ReflectionReuse</c> is the GL-free gate in front of the planar water mirror: it decides
/// whether a frame re-renders the reflection or reuses the cached texture and the view-projection
/// that produced it. The GL half of that pass can only be verified by eye and by the perf-fly
/// protocol in PERF_TODO.md, so every <em>decision</em> the pass makes is pinned here instead —
/// no <c>Camera</c>, no <c>ChunkManager</c>, no window and no GL anywhere in this file.
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
        // The recreated colour texture is TexImage2D'd with null data, so its contents are
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
