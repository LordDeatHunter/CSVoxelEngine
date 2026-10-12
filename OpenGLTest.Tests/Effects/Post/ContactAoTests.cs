using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <see cref="ContactAo"/> mirrors contact-ao-fragment.glsl. Scenes are analytic surfaces seen
/// through a camera built the way <see cref="Camera"/> builds one; every tap's depth is the
/// forward projection of the nearest ray hit, so it inverts through <see cref="ContactAo.Reconstruct"/>
/// exactly like the real depth buffer. The rules: a plane never darkens even at a grazing angle, an
/// inside corner does, a convex edge and a silhouette against far terrain do not, nothing past
/// FadeFar is touched, and the darkening never exceeds Strength.
/// </summary>
public sealed class ContactAoTests
{
    private const float Width = 1920f;
    private const float Height = 1080f;
    private const float FovY = 70f * MathF.PI / 180f;
    private static readonly ContactAoSettings S = ContactAoSettings.Default;

    private readonly record struct View(Matrix4x4 ViewProj, Matrix4x4 Inv, Vector3 Eye);

    private static View LookAt(Vector3 eye, Vector3 target)
    {
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(FovY, Width / Height, 0.2f, 4096f);
        var vp = view * proj;
        Assert.True(Matrix4x4.Invert(vp, out var inv));
        return new View(vp, inv, eye);
    }

    private static Vector2 Project(View v, Vector3 p)
    {
        var c = Vector4.Transform(new Vector4(p, 1f), v.ViewProj);
        return new Vector2(c.X / c.W * 0.5f + 0.5f, c.Y / c.W * 0.5f + 0.5f);
    }

    private static float DepthOf(View v, Vector3 p)
    {
        var c = Vector4.Transform(new Vector4(p, 1f), v.ViewProj);
        return c.Z / c.W * 0.5f + 0.5f;
    }

    /// <summary>A scene is a ray caster: the distance to the nearest hit, or null for the sky.</summary>
    private delegate float? Scene(Vector3 origin, Vector3 dir);

    private static float? Plane(Vector3 o, Vector3 d, Vector3 n, float offset, Func<Vector3, bool> inside)
    {
        var denom = Vector3.Dot(n, d);
        if (MathF.Abs(denom) < 1e-7f) return null;
        var t = (offset - Vector3.Dot(n, o)) / denom;
        if (t <= 0f || !inside(o + d * t)) return null;
        return t;
    }

    private static float? Nearest(params float?[] hits)
    {
        float? best = null;
        foreach (var h in hits)
            if (h is { } t && (best == null || t < best)) best = t;
        return best;
    }

    private static float DepthAt(View v, Scene scene, Vector2 uv)
    {
        var dir = Vector3.Normalize(ContactAo.Reconstruct(v.Inv, uv, 0.75f) - v.Eye);
        return scene(v.Eye, dir) is { } t ? DepthOf(v, v.Eye + dir * t) : 1f;
    }

    private static float OcclusionAt(View v, Scene scene, Vector3 target, in ContactAoSettings s) =>
        OcclusionAtUv(v, scene, Project(v, target), s);

    /// <summary>The pass at one pixel: the centre, its taps a rounded tap radius away and its four
    /// 1-pixel neighbors, read the way the shader reads them.</summary>
    private static float OcclusionAtUv(View v, Scene scene, Vector2 uv, in ContactAoSettings s)
    {
        var dC = DepthAt(v, scene, uv);
        var dist = Vector3.Distance(ContactAo.Reconstruct(v.Inv, uv, dC), v.Eye);
        var pxPerBlock = ContactAo.PxPerBlockAtUnitDist(Height, FovY);
        var r = MathF.Round(ContactAo.PixelRadius(dist, s.RadiusBlocks, pxPerBlock, s.MaxPx));
        float At(float x, float y) => DepthAt(v, scene, uv + new Vector2(x / Width, y / Height));
        var taps = new ContactAoTaps(dC, At(-r, 0f), At(r, 0f), At(0f, -r), At(0f, r),
            At(-1f, 0f), At(1f, 0f), At(0f, -1f), At(0f, 1f));
        return ContactAo.Occlusion(v.Inv, v.Eye, uv, taps, new Vector2(r, r), new Vector2(Width, Height), pxPerBlock, s);
    }

    private static Scene Floor => (o, d) => Plane(o, d, Vector3.UnitY, 0f, _ => true);

    /// <summary>Floor y = 0 in front of a wall z = wallZ facing the camera: an inside corner.</summary>
    private static Scene Corner(float wallZ) => (o, d) => Nearest(
        Plane(o, d, Vector3.UnitY, 0f, p => p.Z >= wallZ),
        Plane(o, d, Vector3.UnitZ, wallZ, p => p.Y >= 0f));

    private const float PostHalf = 1f / 16f;
    private const float PostHeight = 0.625f;

    /// <summary>The floor with a thin square post standing on it at (0, postZ), a torch's width
    /// and height.</summary>
    private static Scene Post(float postZ) => (o, d) => Nearest(
        Plane(o, d, Vector3.UnitY, 0f, _ => true),
        Plane(o, d, Vector3.UnitZ, postZ + PostHalf, p => MathF.Abs(p.X) <= PostHalf && p.Y is >= 0f and <= PostHeight),
        Plane(o, d, Vector3.UnitX, -PostHalf, p => MathF.Abs(p.Z - postZ) <= PostHalf && p.Y is >= 0f and <= PostHeight),
        Plane(o, d, Vector3.UnitX, PostHalf, p => MathF.Abs(p.Z - postZ) <= PostHalf && p.Y is >= 0f and <= PostHeight),
        Plane(o, d, Vector3.UnitY, PostHeight, p => MathF.Abs(p.X) <= PostHalf && MathF.Abs(p.Z - postZ) <= PostHalf));

    /// <summary>Every floor pixel in a window around the post, with how far its floor point lies
    /// from the post's footprint and its occlusion.</summary>
    private static IEnumerable<(float FromPost, float Occlusion)> FloorAroundPost(View v, float postZ, ContactAoSettings s)
    {
        var scene = Post(postZ);
        var baseUv = Project(v, new Vector3(0f, 0f, postZ + PostHalf));
        var topUv = Project(v, new Vector3(0f, PostHeight, postZ - PostHalf));
        var top = (topUv.Y - baseUv.Y) * Height;
        for (var y = -30f; y <= top + 60f; y += 2f)
        for (var x = -90f; x <= 90f; x += 2f)
        {
            var uv = baseUv + new Vector2(x / Width, y / Height);
            var p = ContactAo.Reconstruct(v.Inv, uv, DepthAt(v, scene, uv));
            if (p.Y > 1e-3f) continue; // on the post
            var dx = MathF.Max(MathF.Abs(p.X) - PostHalf, 0f);
            var dz = MathF.Max(MathF.Abs(p.Z - postZ) - PostHalf, 0f);
            yield return (MathF.Sqrt(dx * dx + dz * dz), OcclusionAtUv(v, scene, uv, s));
        }
    }

    private static readonly (Vector3 Eye, float PostZ)[] PostViews =
    [
        (new Vector3(0f, 3.5f, 0f), -4f),
        (new Vector3(0f, 1.7f, 0f), -6f),
        (new Vector3(0.8f, 5f, 0f), -10f),
    ];

    private static View PostView((Vector3 Eye, float PostZ) pv) =>
        LookAt(pv.Eye, new Vector3(0f, PostHeight * 0.5f, pv.PostZ));

    [Fact]
    public void FlatPlaneAtGrazingAngleIsNotOccluded()
    {
        var v = LookAt(new Vector3(0f, 1.7f, 0f), new Vector3(0f, 0f, -20f));
        foreach (var z in new[] { -4f, -10f, -20f, -40f })
            Assert.Equal(0f, OcclusionAt(v, Floor, new Vector3(0.3f, 0f, z), S), 4);
    }

    [Fact]
    public void InsideCornerIsOccluded()
    {
        foreach (var wallZ in new[] { -5f, -12f, -20f })
        {
            var v = LookAt(new Vector3(0f, 1.7f, 0f), new Vector3(0f, 0f, wallZ));
            Assert.True(OcclusionAt(v, Corner(wallZ), new Vector3(0f, 0f, wallZ), S) > 0f);
        }
    }

    [Fact]
    public void ConvexEdgeIsNotOccluded()
    {
        // The top front edge of a step: its top face y = 1 behind z = -6, its front face z = -6
        // below y = 1, and the floor in front of it.
        Scene step = (o, d) => Nearest(
            Plane(o, d, Vector3.UnitY, 1f, p => p.Z <= -6f),
            Plane(o, d, Vector3.UnitZ, -6f, p => p.Y is >= 0f and <= 1f),
            Plane(o, d, Vector3.UnitY, 0f, p => p.Z >= -6f));
        var v = LookAt(new Vector3(0f, 2.5f, 0f), new Vector3(0f, 1f, -6f));
        Assert.Equal(0f, OcclusionAt(v, step, new Vector3(0f, 1f, -6f), S), 4);
    }

    [Fact]
    public void DistantSilhouetteIsNotOccluded()
    {
        // A near wall ending at x = 0 in front of a far backdrop: the backdrop pixel just past the
        // wall's edge has one tap on the near wall.
        Scene scene = (o, d) => Nearest(
            Plane(o, d, Vector3.UnitZ, -3f, p => p.X <= 0f),
            Plane(o, d, Vector3.UnitZ, -20f, _ => true));
        var v = LookAt(new Vector3(0f, 1f, 0f), new Vector3(0f, 1f, -20f));
        var edgeUv = Project(v, new Vector3(0f, 1f, -3f));
        // One pixel right of the edge, still on the backdrop.
        var target = ContactAo.Reconstruct(v.Inv, edgeUv + new Vector2(1f / Width, 0f), DepthOf(v, new Vector3(0.5f, 1f, -20f)));
        var dir = Vector3.Normalize(target - v.Eye);
        var onBackdrop = v.Eye + dir * (scene(v.Eye, dir) ?? 0f);
        Assert.Equal(-20f, onBackdrop.Z, 3);
        Assert.Equal(0f, OcclusionAt(v, scene, onBackdrop, S), 4);
    }

    [Fact]
    public void BeyondFadeFarIsNotOccluded()
    {
        // Seen from above so the corner is well past FadeFar yet still a moderate crease.
        var wallZ = -(S.FadeFar + 16f);
        var v = LookAt(new Vector3(0f, -0.75f * wallZ, 0f), new Vector3(0f, 0f, wallZ));
        var target = new Vector3(0f, 0f, wallZ);
        Assert.Equal(0f, OcclusionAt(v, Corner(wallZ), target, S));
        // The same corner does crease: it is the distance fade that leaves it alone.
        var noFade = S with { FadeNear = 1e5f, FadeFar = 2e5f };
        Assert.True(OcclusionAt(v, Corner(wallZ), target, noFade) > 0f);
    }

    [Fact]
    public void AThinPostLeavesNoGhostOnTheFloorBesideIt()
    {
        // Floor pixels a tap radius beside the post have a tap on it; past the reach (with slack
        // for the rounded pixel radius) none of them may darken, while the plain midpoint crease
        // without the reach does darken them: it is the reach that removes the ghost.
        var beyondReach = 1.25f * S.ReachFar * S.RadiusBlocks;
        var noReach = S with { ReachNear = 1e5f, ReachFar = 2e5f };
        foreach (var pv in PostViews)
        {
            var v = PostView(pv);
            var ghost = 0f;
            foreach (var (fromPost, occ) in FloorAroundPost(v, pv.PostZ, S))
                if (fromPost > beyondReach) Assert.Equal(0f, occ, 4);
            foreach (var (fromPost, occ) in FloorAroundPost(v, pv.PostZ, noReach))
                if (fromPost > beyondReach) ghost = MathF.Max(ghost, occ);
            Assert.True(ghost > 0.25f * S.Strength, $"no ghost to remove from {pv.Eye}");
        }
    }

    [Fact]
    public void AThinPostStillDarkensTheFloorAtItsFoot()
    {
        foreach (var pv in PostViews)
        {
            var v = PostView(pv);
            var foot = 0f;
            foreach (var (fromPost, occ) in FloorAroundPost(v, pv.PostZ, S))
                if (fromPost < S.RadiusBlocks) foot = MathF.Max(foot, occ);
            Assert.True(foot > 0f, $"no contact AO at the foot from {pv.Eye}");
        }
    }

    [Fact]
    public void OcclusionNeverExceedsStrength()
    {
        var v = LookAt(new Vector3(0f, 1.7f, 0f), new Vector3(3f, 0f, -8f));
        var pxPerBlock = ContactAo.PxPerBlockAtUnitDist(Height, FovY);
        var rng = new Random(1234);
        for (var i = 0; i < 20000; i++)
        {
            var uv = new Vector2(rng.NextSingle(), rng.NextSingle());
            float D() => 0.5f + 0.5f * rng.NextSingle();
            var taps = new ContactAoTaps(D(), D(), D(), D(), D(), D(), D(), D(), D());
            var tapPx = new Vector2(rng.Next(1, 33), rng.Next(1, 33));
            var occ = ContactAo.Occlusion(v.Inv, v.Eye, uv, taps, tapPx, new Vector2(Width, Height), pxPerBlock, S);
            Assert.InRange(occ, 0f, S.Strength);
        }
    }
}
