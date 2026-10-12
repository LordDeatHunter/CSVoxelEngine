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
        new(WorldGenerationConfig.ShadowRadiusBlocks, WorldGenerationConfig.ShadowMapSize);

    private static ShadowCascade Far() =>
        new(WorldGenerationConfig.FarShadowRadiusBlocks, WorldGenerationConfig.FarShadowMapSize);

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
        var (right, up) = ShadowCenterTests.LightAxes(sun);
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

    [Fact]
    public void TheNearBoxsFadeBandLiesInsideTheFarBoxsUnfadedCore()
    {
        Vector3[] eyes = [new(0f, 70f, 0f), new(-40f, -107f, -75f), new(5000f, 60f, -7000f)];
        float[] boundary = [-1f, 1f];
        float[] depths = [0f, 0.5f, 1f];
        float[] along = [-1f, -0.5f, 0f, 0.5f, 1f];

        foreach (var sun in ShadowCenterTests.Suns)
        foreach (var eye in eyes)
        foreach (var yaw in new[] { 0f, 90f, 180f, 270f })
        foreach (var pitch in new[] { -60f, 0f, 45f })
        {
            var viewDir = ViewDir(yaw, pitch);
            var near = Near();
            var far = Far();
            near.Update(eye, viewDir, sun);
            far.Update(eye, viewDir, sun);
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
                        $"near-box edge {ndc} lands at far NDC {f} (sun {sun}, eye {eye}, yaw {yaw}, pitch {pitch})");
                    Assert.InRange(f.Z, 0f, 1f);
                }
            }
        }
    }
}
