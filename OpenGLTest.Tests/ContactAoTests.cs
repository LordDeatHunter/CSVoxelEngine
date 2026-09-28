using System;
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

    private static float OcclusionAt(View v, Scene scene, Vector3 target, in ContactAoSettings s)
    {
        var uv = Project(v, target);
        var dC = DepthAt(v, scene, uv);
        var dist = Vector3.Distance(ContactAo.Reconstruct(v.Inv, uv, dC), v.Eye);
        var r = ContactAo.PixelRadius(dist, s.RadiusBlocks, ContactAo.PxPerBlockAtUnitDist(Height, FovY), s.MaxPx);
        var dx = new Vector2(r / Width, 0f);
        var dy = new Vector2(0f, r / Height);
        return ContactAo.Occlusion(v.Inv, v.Eye, uv, dC,
            DepthAt(v, scene, uv - dx), DepthAt(v, scene, uv + dx),
            DepthAt(v, scene, uv - dy), DepthAt(v, scene, uv + dy), s);
    }

    private static Scene Floor => (o, d) => Plane(o, d, Vector3.UnitY, 0f, _ => true);

    /// <summary>Floor y = 0 in front of a wall z = wallZ facing the camera: an inside corner.</summary>
    private static Scene Corner(float wallZ) => (o, d) => Nearest(
        Plane(o, d, Vector3.UnitY, 0f, p => p.Z >= wallZ),
        Plane(o, d, Vector3.UnitZ, wallZ, p => p.Y >= 0f));

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
    public void OcclusionNeverExceedsStrength()
    {
        var v = LookAt(new Vector3(0f, 1.7f, 0f), new Vector3(3f, 0f, -8f));
        var rng = new Random(1234);
        for (var i = 0; i < 20000; i++)
        {
            var uv = new Vector2(rng.NextSingle(), rng.NextSingle());
            float D() => 0.5f + 0.5f * rng.NextSingle();
            var occ = ContactAo.Occlusion(v.Inv, v.Eye, uv, D(), D(), D(), D(), D(), S);
            Assert.InRange(occ, 0f, S.Strength);
        }
    }
}
