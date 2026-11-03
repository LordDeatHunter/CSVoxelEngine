using System;
using System.Numerics;
using VoxelEngine.Rendering;
using Xunit;

namespace VoxelEngine.Tests.Rendering;

/// <summary>
/// The two shadow cascades together: each steps its own center on its own texel lattice, and the
/// near box's fade band always lies inside the far box's unfaded core, so <c>SampleShadow</c>'s
/// hand-over from near to far never passes through a region where neither cascade applies (a
/// bright ring around the near box). GL-free: the cascades are constructed and updated but never
/// <see cref="ShadowCascade.Create"/>d.
/// </summary>
public sealed class ShadowCascadeTests
{
    private static ShadowCascade Near() =>
        new(LightingConfig.ShadowRadiusBlocks, LightingConfig.ShadowMapSize);

    private static ShadowCascade Far() =>
        new(LightingConfig.FarShadowRadiusBlocks, LightingConfig.FarShadowMapSize);

    /// <summary>Where <c>SampleShadow</c> starts fading a cascade out, in NDC of its box.</summary>
    private const float FadeStart = 0.85f;

    private static Vector3 ViewDir(float yawDeg, float pitchDeg)
    {
        var yaw = yawDeg * MathF.PI / 180f;
        var pitch = pitchDeg * MathF.PI / 180f;
        return Vector3.Normalize(new Vector3(
            MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Sin(yaw) * MathF.Cos(pitch)));
    }

    [Fact]
    public void TheCascadesSnapIndependently()
    {
        var sun = ShadowCenterTests.SunAt(2.1f);
        var (right, up) = ShadowCascade.LightAxes(sun);
        var near = Near();
        var far = Far();
        var nearTexel = near.WorldTexel;
        var farTexel = far.WorldTexel;
        var viewDir = ViewDir(30f, -10f);
        var eye = new Vector3(40.5f, 70f, -12.25f);
        var step = new Vector3(0.0125f, 0.004f, 0.0075f);

        near.Update(eye, viewDir, sun);
        far.Update(eye, viewDir, sun);
        int nearMoves = 0, farMoves = 0;
        // Moves that are NOT whole texels of the other cascade's lattice: if either cascade snapped
        // to the other's texel, every one of its moves would be a whole texel of that lattice too.
        int nearOffFarLattice = 0, farOffNearLattice = 0;

        for (var frame = 0; frame < 1500; frame++)
        {
            eye += step;
            var nearBefore = near.Center;
            var farBefore = far.Center;
            near.Update(eye, viewDir, sun);
            far.Update(eye, viewDir, sun);
            var dn = near.Center - nearBefore;
            var df = far.Center - farBefore;

            Assert.InRange(MathF.Abs(ShadowCenterTests.TexelFraction(dn, right, nearTexel)), 0f, 0.02f);
            Assert.InRange(MathF.Abs(ShadowCenterTests.TexelFraction(dn, up, nearTexel)), 0f, 0.02f);
            Assert.InRange(MathF.Abs(ShadowCenterTests.TexelFraction(df, right, farTexel)), 0f, 0.02f);
            Assert.InRange(MathF.Abs(ShadowCenterTests.TexelFraction(df, up, farTexel)), 0f, 0.02f);

            if (LateralLength(dn, sun) > nearTexel * 0.5f)
            {
                nearMoves++;
                if (OffLattice(dn, right, up, farTexel)) nearOffFarLattice++;
            }
            if (LateralLength(df, sun) > farTexel * 0.5f)
            {
                farMoves++;
                if (OffLattice(df, right, up, nearTexel)) farOffNearLattice++;
            }
        }

        Assert.True(nearMoves > 10, $"near cascade moved only {nearMoves} times");
        Assert.True(farMoves > 10, $"far cascade moved only {farMoves} times");
        // The coarser lattice moves in bigger steps, so less often.
        Assert.True(farMoves < nearMoves, $"far moved {farMoves} times, near {nearMoves}");
        Assert.True(nearOffFarLattice > 0, "every near move was a whole far texel");
        Assert.True(farOffNearLattice > 0, "every far move was a whole near texel");
    }

    private static float LateralLength(Vector3 delta, Vector3 sun) =>
        (delta - sun * Vector3.Dot(delta, sun)).Length();

    private static bool OffLattice(Vector3 delta, Vector3 right, Vector3 up, float texel) =>
        MathF.Abs(ShadowCenterTests.TexelFraction(delta, right, texel)) > 0.05f
        || MathF.Abs(ShadowCenterTests.TexelFraction(delta, up, texel)) > 0.05f;

    private static readonly Vector3[] Eyes = [new(0f, 70f, 0f), new(-40f, -107f, -75f), new(5000f, 60f, -7000f)];
    private static readonly float[] Yaws = [0f, 90f, 180f, 270f];
    private static readonly float[] Pitches = [-60f, 0f, 45f];

    [Fact]
    public void TheNearBoxsFadeBandLiesInsideTheFarBoxsUnfadedCore()
    {
        foreach (var sun in ShadowCenterTests.Suns)
        foreach (var eye in Eyes)
        foreach (var yaw in Yaws)
        foreach (var pitch in Pitches)
        {
            var viewDir = ViewDir(yaw, pitch);
            var near = Near();
            var far = Far();
            near.Update(eye, viewDir, sun);
            far.Update(eye, viewDir, sun);
            AssertNearFadeBandInsideFarCore(near, far, $"sun {sun}, eye {eye}, yaw {yaw}, pitch {pitch}");
        }
    }

    [Fact]
    public void TheNearBoxsFadeBandLiesInsideTheFarCoreWhileTheFarCenterIsHeld()
    {
        var inner = 0.98f * LightingConfig.FarShadowReuseInnerFraction * LightingConfig.FarShadowRadiusBlocks;
        var depthMargin = 0.98f * LightingConfig.FarShadowReuseDepthMarginFraction * LightingConfig.FarShadowRadiusBlocks;

        foreach (var sun in ShadowCenterTests.Suns)
        {
            var (right, up) = ShadowCascade.LightAxes(sun);
            // The target pushed to the inner region's edge along each lateral axis and to its corners
            // (the region is a square in the max norm, so a corner is its farthest point).
            Vector3[] lateral =
            [
                right * inner, -right * inner, up * inner, -up * inner,
                (right + up) * inner, (right - up) * inner, (-right + up) * inner, (-right - up) * inner,
            ];
            float[] alongSun = [-depthMargin, 0f, depthMargin];

            foreach (var eye0 in Eyes)
            foreach (var yaw in Yaws)
            foreach (var pitch in Pitches)
            {
                var viewDir = ViewDir(yaw, pitch);
                foreach (var shift in lateral)
                foreach (var along in alongSun)
                {
                    var near = Near();
                    var far = Far();
                    far.UpdateReused(eye0, viewDir, sun, moonLit: false, geometryVersion: 0);
                    Assert.True(far.RenderThisFrame);
                    far.MarkRendered();

                    // The view is unchanged, so the target moves exactly as the eye does.
                    var eye = eye0 + shift + sun * along;
                    near.Update(eye, viewDir, sun);
                    far.UpdateReused(eye, viewDir, sun, moonLit: false, geometryVersion: 0);

                    var context = $"sun {sun}, eye0 {eye0}, yaw {yaw}, pitch {pitch}, shift {shift}, along {along}";
                    Assert.False(far.RenderThisFrame, $"far map re-rendered inside the reuse bounds ({context})");
                    AssertNearFadeBandInsideFarCore(near, far, context);
                }
            }
        }
    }

    /// <summary>Every point on the near box's lateral edges, at every depth, lands inside the far
    /// box's unfaded core and inside its depth range.</summary>
    private static void AssertNearFadeBandInsideFarCore(ShadowCascade near, ShadowCascade far, string context)
    {
        float[] boundary = [-1f, 1f];
        float[] depths = [0f, 0.5f, 1f];
        float[] along = [-1f, -0.5f, 0f, 0.5f, 1f];
        Assert.True(Matrix4x4.Invert(near.ViewProj, out var invNear));

        foreach (var edge in boundary)
        foreach (var t in along)
        foreach (var z in depths)
        {
            // Both lateral edges of the near box: x = +-1 swept along y, and y = +-1 along x.
            foreach (var ndc in new[] { new Vector3(edge, t, z), new Vector3(t, edge, z) })
            {
                var world = Vector4.Transform(new Vector4(ndc, 1f), invNear);
                var p = new Vector3(world.X, world.Y, world.Z) / world.W;
                var clip = Vector4.Transform(new Vector4(p, 1f), far.ViewProj);
                var f = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;

                Assert.True(MathF.Max(MathF.Abs(f.X), MathF.Abs(f.Y)) < FadeStart,
                    $"near-box edge {ndc} lands at far NDC {f} ({context})");
                Assert.InRange(f.Z, 0f, 1f);
            }
        }
    }

    [Fact]
    public void AStillCameraRendersTheFarMapOnceThenReusesItsViewProj()
    {
        var sun = ShadowCenterTests.SunAt(1.7f);
        var eye = new Vector3(120.25f, 64f, -33.5f);
        var viewDir = ViewDir(40f, -15f);
        var far = Far();

        Assert.Equal(FarShadowReuse.Refresh.NoMap, far.UpdateReused(eye, viewDir, sun, false, 7));
        Assert.True(far.RenderThisFrame);
        far.MarkRendered();
        var held = far.ViewProj;

        var reusedInARow = 0;
        for (var frame = 0; frame < 3 * LightingConfig.FarShadowReuseMaxFrames; frame++)
        {
            var why = far.UpdateReused(eye, viewDir, sun, false, 7);
            if (!far.RenderThisFrame)
            {
                Assert.Equal(FarShadowReuse.Refresh.None, why);
                Assert.Equal(held, far.ViewProj);
                reusedInARow++;
                continue;
            }

            // Nothing changed, so only the age may force a render, and only once the map is that old.
            Assert.Equal(FarShadowReuse.Refresh.MaxAge, why);
            Assert.Equal(LightingConfig.FarShadowReuseMaxFrames - 1, reusedInARow);
            far.MarkRendered();
            held = far.ViewProj;
            reusedInARow = 0;
        }
    }

    [Fact]
    public void AnUnconfirmedRenderIsRetriedTheNextFrame()
    {
        var sun = ShadowCenterTests.SunAt(2.4f);
        var viewDir = ViewDir(0f, 0f);
        var eye = new Vector3(10f, 70f, 10f);
        var far = Far();

        far.UpdateReused(eye, viewDir, sun, false, 0);
        far.MarkRendered();

        // Jump the eye far enough sideways to force a re-center, then never confirm that render.
        var (right, _) = ShadowCascade.LightAxes(sun);
        var moved = eye + right * (2f * LightingConfig.FarShadowReuseInnerFraction * LightingConfig.FarShadowRadiusBlocks);
        Assert.Equal(FarShadowReuse.Refresh.Recenter, far.UpdateReused(moved, viewDir, sun, false, 0));
        Assert.True(far.RenderThisFrame);

        Assert.Equal(FarShadowReuse.Refresh.NoMap, far.UpdateReused(moved, viewDir, sun, false, 0));
        Assert.True(far.RenderThisFrame);
    }

    [Fact]
    public void InvalidateForcesTheNextUpdateToRender()
    {
        var sun = ShadowCenterTests.SunAt(1.2f);
        var viewDir = ViewDir(90f, 10f);
        var eye = new Vector3(-300f, 80f, 45f);
        var far = Far();

        far.UpdateReused(eye, viewDir, sun, false, 3);
        far.MarkRendered();
        Assert.Equal(FarShadowReuse.Refresh.None, far.UpdateReused(eye, viewDir, sun, false, 3));
        Assert.False(far.RenderThisFrame);

        far.Invalidate();
        Assert.False(far.RenderThisFrame);
        Assert.Equal(FarShadowReuse.Refresh.NoMap, far.UpdateReused(eye, viewDir, sun, false, 3));
        Assert.True(far.RenderThisFrame);
    }

    [Fact]
    public void TheFarCenterMovesOnlyOnRenderFramesAndInWholeTexels()
    {
        var sun = ShadowCenterTests.SunAt(2.1f);
        var (right, up) = ShadowCascade.LightAxes(sun);
        var far = Far();
        var texel = far.WorldTexel;
        var viewDir = ViewDir(30f, -10f);
        var eye = new Vector3(40.5f, 70f, -12.25f);
        // Across the light, so the eye never drifts out of the depth margin, and fast enough that the
        // target leaves the inner region in about half the max age: both held frames and re-centers.
        var stepLength = 2f * LightingConfig.FarShadowReuseInnerFraction * LightingConfig.FarShadowRadiusBlocks
                         / LightingConfig.FarShadowReuseMaxFrames;
        var step = Vector3.Normalize(right + 0.3f * up) * stepLength;

        far.UpdateReused(eye, viewDir, sun, false, 0);
        far.MarkRendered();
        int held = 0, recenters = 0;

        for (var frame = 0; frame < 600; frame++)
        {
            eye += step;
            var before = far.Center;
            var viewProjBefore = far.ViewProj;
            var why = far.UpdateReused(eye, viewDir, sun, false, 0);

            if (!far.RenderThisFrame)
            {
                Assert.Equal(before, far.Center);
                Assert.Equal(viewProjBefore, far.ViewProj);
                held++;
                continue;
            }

            if (why == FarShadowReuse.Refresh.Recenter)
                recenters++;
            var moved = far.Center - before;
            Assert.InRange(MathF.Abs(ShadowCenterTests.TexelFraction(moved, right, texel)), 0f, 0.02f);
            Assert.InRange(MathF.Abs(ShadowCenterTests.TexelFraction(moved, up, texel)), 0f, 0.02f);
            far.MarkRendered();
        }

        Assert.True(held > 0, "the far center was never held");
        Assert.True(recenters > 0, "the far center never re-centered");
    }
}
