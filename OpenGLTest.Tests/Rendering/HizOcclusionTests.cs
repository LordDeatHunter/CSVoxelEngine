using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Player;
using VoxelEngine.Rendering;
using Xunit;

namespace VoxelEngine.Tests.Rendering;

/// <summary>
/// <see cref="HizOcclusion"/> and <see cref="HizPyramid"/> decide which chunks the opaque pass may
/// skip, and the skip must be exact: a chunk may only be dropped when every fragment it could
/// rasterize lies strictly behind depth already in the frame. These facts check the rect and depth
/// bounds against points sampled straight from the boxes (never from the helper's own corner math),
/// the pyramid against a brute-force max, and the whole test against an analytically rasterized wall.
/// </summary>
public sealed class HizOcclusionTests
{
    private const int Width = 320;
    private const int Height = 180;

    /// <summary>Camera poses: eye, yaw, pitch, roll (radians), vertical FOV (degrees), aspect.</summary>
    private static readonly (Vector3 Eye, float Yaw, float Pitch, float Roll, float Fov, float Aspect)[] Poses =
    [
        (new Vector3(8f, 70f, 8f), 0f, 0f, 0f, 70f, 16f / 9f),
        (new Vector3(-120.5f, 40f, 300.25f), 2.1f, -0.6f, 0.05f, 90f, 21f / 9f),
        (new Vector3(2000f, 95f, -1500f), -1.3f, 1.2f, -0.1f, 55f, 4f / 3f),
        (new Vector3(30f, 64f, -45f), 3.5f, -1.5f, 0f, 75f, 1f),
        (new Vector3(-7f, 120f, 19f), 0.7f, 0.3f, 0.3f, 100f, 0.6f),
    ];

    private static Vector3 ViewDir(float yaw, float pitch) =>
        new(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Sin(yaw));

    /// <summary>A view the way <c>Camera.GetViewMatrix</c> builds one: roll turns the up vector.</summary>
    private static Matrix4x4 View(Vector3 eye, Vector3 dir, float roll)
    {
        var up = Vector3.Transform(Vector3.UnitY, Matrix4x4.CreateFromAxisAngle(dir, roll));
        return Matrix4x4.CreateLookAt(eye, eye + dir, up);
    }

    // Camera.NearPlane (private) is 0.2; the far plane is the camera's own.
    private static Matrix4x4 Projection(float fovDegrees, float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(fovDegrees * MathF.PI / 180f, aspect, 0.2f, Camera.FarPlane);

    private static Matrix4x4 PoseViewProj(int pose, out Vector3 eye, out Vector3 dir)
    {
        var p = Poses[pose];
        eye = p.Eye;
        dir = ViewDir(p.Yaw, p.Pitch);
        return View(eye, dir, p.Roll) * Projection(p.Fov, p.Aspect);
    }

    private static float Range(Random rng, float min, float max) => min + (float)rng.NextDouble() * (max - min);

    /// <summary>A unit vector perpendicular to <paramref name="dir"/>.</summary>
    private static Vector3 Side(Vector3 dir) =>
        Vector3.Normalize(Vector3.Cross(dir, MathF.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));

    /// <summary>
    /// Asserts that every sampled point of the box whose pixel is on the frame lands inside the rect
    /// with a window depth no nearer than the bound (less the half of the margin the CPU side may
    /// spend on float error). Returns how many boxes had bounds.
    /// </summary>
    private static int CheckBoxesContainTheirPoints(Random rng, in Matrix4x4 viewProj, Func<Random, (Vector3 Min, Vector3 Max)> box,
        int width, int height)
    {
        var bounded = 0;
        for (var b = 0; b < 300; b++)
        {
            var (min, max) = box(rng);
            if (!HizOcclusion.TryScreenBounds(viewProj, min, max, width, height, out var rect, out var near)) continue;
            bounded++;
            for (var s = 0; s < 60; s++)
            {
                var p = new Vector3(Range(rng, min.X, max.X), Range(rng, min.Y, max.Y), Range(rng, min.Z, max.Z));
                var c = Vector4.Transform(new Vector4(p, 1f), viewProj);
                Assert.True(c.W > 0f);
                var sx = (c.X / c.W * 0.5f + 0.5f) * width;
                var sy = (c.Y / c.W * 0.5f + 0.5f) * height;
                if (sx < 0f || sy < 0f || sx >= width || sy >= height) continue;
                var px = (int)MathF.Floor(sx);
                var py = (int)MathF.Floor(sy);
                Assert.InRange(px, rect.X0, rect.X1);
                Assert.InRange(py, rect.Y0, rect.Y1);
                Assert.True(0.5f * c.Z / c.W + 0.5f >= near - HizOcclusion.DepthMargin / 2f,
                    $"a point of the box is nearer than its bound ({near})");
            }
        }

        return bounded;
    }

    [Fact]
    public void ProjectedRectAndNearDepthContainEveryPointOfTheBox()
    {
        var rng = new Random(9);
        for (var pose = 0; pose < Poses.Length; pose++)
        {
            var viewProj = PoseViewProj(pose, out var eye, out var dir);
            var side = Side(dir);
            var up = Vector3.Cross(side, dir);
            var bounded = CheckBoxesContainTheirPoints(rng, viewProj, r =>
            {
                var center = eye + dir * Range(r, 2f, 600f) + side * Range(r, -300f, 300f) + up * Range(r, -200f, 200f);
                var half = new Vector3(Range(r, 0.2f, 20f), Range(r, 0.2f, 20f), Range(r, 0.2f, 20f));
                return (center - half, center + half);
            }, Width, Height);
            Assert.True(bounded > 50, $"pose {pose}: too few boxes on screen to mean anything ({bounded})");
        }
    }

    [Fact]
    public void ABoxTouchingTheEyePlaneMustBeDrawn()
    {
        for (var pose = 0; pose < Poses.Length; pose++)
        {
            var viewProj = PoseViewProj(pose, out var eye, out var dir);
            var side = Side(dir);

            // Containing the eye.
            Assert.False(HizOcclusion.TryScreenBounds(viewProj, eye - Vector3.One, eye + Vector3.One, Width, Height, out _, out _));

            // Off to the side, reaching from in front of the eye to behind it.
            var center = eye + side * 5f;
            var a = center + dir * 3f;
            var b = center - dir * 3f;
            Assert.False(HizOcclusion.TryScreenBounds(viewProj, Vector3.Min(a, b) - new Vector3(0.5f), Vector3.Max(a, b) + new Vector3(0.5f),
                Width, Height, out _, out _));

            // Wholly in front of the eye and on screen: bounded.
            var front = eye + dir * 30f;
            Assert.True(HizOcclusion.TryScreenBounds(viewProj, front - Vector3.One, front + Vector3.One, Width, Height, out _, out _));
        }
    }

    [Fact]
    public void PyramidMaxCoversEveryPixelOfTheRect()
    {
        var rng = new Random(17);
        var pyramid = new HizPyramid();
        for (var field = 0; field < 400; field++)
        {
            // One pyramid rebuilt at every size, so a reused (larger) storage must index the same.
            var w = rng.Next(1, 38);
            var h = rng.Next(1, 38);
            var depth = new float[w * h];
            for (var i = 0; i < depth.Length; i++) depth[i] = (float)rng.NextDouble();
            pyramid.Build(depth, w, h);

            for (var r = 0; r < 40; r++)
            {
                int xa = rng.Next(w), xb = rng.Next(w), ya = rng.Next(h), yb = rng.Next(h);
                var rect = new HizRect(Math.Min(xa, xb), Math.Min(ya, yb), Math.Max(xa, xb), Math.Max(ya, yb));
                var brute = 0f;
                for (var y = rect.Y0; y <= rect.Y1; y++)
                for (var x = rect.X0; x <= rect.X1; x++)
                    brute = MathF.Max(brute, depth[y * w + x]);
                Assert.True(pyramid.MaxOver(rect) >= brute, $"{w}x{h} rect {rect}: pyramid max below a pixel's depth");
            }
        }
    }

    [Fact]
    public void OcclusionIsStrictlyBehindTheMargin()
    {
        foreach (var max in new[] { 0.5f, 0.9f, 0.99f, 0.9999f })
        {
            Assert.False(HizOcclusion.IsOccluded(max, max));
            Assert.False(HizOcclusion.IsOccluded(max - 0.01f, max));
            Assert.False(HizOcclusion.IsOccluded(max + HizOcclusion.DepthMargin * 0.5f, max));
            Assert.True(HizOcclusion.IsOccluded(max + HizOcclusion.DepthMargin * 2f, max));
        }

        // Nothing is ever behind the cleared far depth.
        Assert.False(HizOcclusion.IsOccluded(1f, 1f));
    }

    [Fact]
    public void ABoxBehindAWallIsOccludedAndOneInFrontIsNot()
    {
        // The eye at the origin looking down -Z; a wall at z = -20 spanning x in [-12, 12], y in [-8, 8].
        const int w = 160, h = 90;
        const float wallZ = -20f, wallHalfX = 12f, wallHalfY = 8f;
        var viewProj = Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY) * Projection(70f, (float)w / h);
        Assert.True(Matrix4x4.Invert(viewProj, out var inverse));

        // Rasterize: the depth at each pixel center is the window depth of the wall point on that
        // pixel's ray, or the cleared 1 where the ray misses the wall.
        var depth = new float[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var ndcX = 2f * (x + 0.5f) / w - 1f;
            var ndcY = 2f * (y + 0.5f) / h - 1f;
            var nearP = Vector4.Transform(new Vector4(ndcX, ndcY, 0f, 1f), inverse);
            var farP = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), inverse);
            var a = new Vector3(nearP.X, nearP.Y, nearP.Z) / nearP.W;
            var b = new Vector3(farP.X, farP.Y, farP.Z) / farP.W;
            var t = (wallZ - a.Z) / (b.Z - a.Z);
            var hit = a + (b - a) * t;
            depth[y * w + x] = 1f;
            if (MathF.Abs(hit.X) > wallHalfX || MathF.Abs(hit.Y) > wallHalfY) continue;
            var c = Vector4.Transform(new Vector4(hit, 1f), viewProj);
            depth[y * w + x] = 0.5f * c.Z / c.W + 0.5f;
        }

        var pyramid = new HizPyramid();
        pyramid.Build(depth, w, h);

        bool Occluded(Vector3 boxMin, Vector3 boxMax)
        {
            HizOcclusion.GrowAabb(boxMin, boxMax, out var min, out var max);
            return HizOcclusion.TryScreenBounds(viewProj, min, max, w, h, out var rect, out var near)
                   && HizOcclusion.IsOccluded(near, pyramid.MaxOver(rect));
        }

        Assert.True(Occluded(new Vector3(-1f, -1f, -41f), new Vector3(1f, 1f, -39f)), "a box wholly behind the wall");
        Assert.False(Occluded(new Vector3(-1f, -1f, -11f), new Vector3(1f, 1f, -9f)), "a box in front of the wall");
        Assert.False(Occluded(new Vector3(-1f, -1f, -25f), new Vector3(1f, 1f, -15f)), "a box straddling the wall");
        Assert.False(Occluded(new Vector3(20f, -1f, -41f), new Vector3(26f, 1f, -39f)), "a box reaching past the wall's edge");
    }

    [Fact]
    public void BoxesHuggingTheEyePlaneStayConservative()
    {
        // Corners a hair in front of the eye project thousands of frames off screen; the clamp that
        // keeps the rounding finite must never pull the rect off a point that is on screen.
        var rng = new Random(31);
        for (var pose = 0; pose < Poses.Length; pose++)
        {
            var viewProj = PoseViewProj(pose, out var eye, out var dir);
            var side = Side(dir);
            var up = Vector3.Cross(side, dir);
            var bounded = CheckBoxesContainTheirPoints(rng, viewProj, r =>
            {
                var near = Range(r, 0.21f, 1.5f);
                var lateral = side * Range(r, -6f, 6f) + up * Range(r, -6f, 6f);
                var a = eye + dir * near + lateral;
                var b = a + dir * Range(r, 0.01f, 20f) + side * Range(r, -8f, 8f) + up * Range(r, -8f, 8f);
                return (Vector3.Min(a, b), Vector3.Max(a, b));
            }, Width, Height);
            Assert.True(bounded > 20, $"pose {pose}: too few near boxes on screen to mean anything ({bounded})");
        }
    }

    [Fact]
    public void SegmentStartsSplitTheRunIntoNonEmptyOrderedSegments()
    {
        var rng = new Random(41);
        Span<int> starts = stackalloc int[8];
        for (var trial = 0; trial < 2000; trial++)
        {
            var count = rng.Next(0, 3000);
            var fractions = new float[rng.Next(0, 7)];
            for (var i = 0; i < fractions.Length; i++)
                fractions[i] = rng.Next(4) == 0 ? rng.Next(2) : (float)rng.NextDouble();
            Array.Sort(fractions);

            var written = HizOcclusion.SegmentStarts(count, fractions, starts);

            // Every start opens a non-empty segment after a non-empty one, in order.
            var previous = 0;
            for (var i = 0; i < written; i++)
            {
                Assert.True(starts[i] > previous && starts[i] < count, $"start {starts[i]} of {count}");
                previous = starts[i];
            }

            // And exactly the distinct splits the fractions name inside the run are taken.
            var expected = new SortedSet<int>();
            foreach (var f in fractions)
            {
                var at = (int)(count * f);
                if (at > 0 && at < count) expected.Add(at);
            }

            Assert.Equal(expected.ToArray(), starts[..written].ToArray());
        }
    }
}
