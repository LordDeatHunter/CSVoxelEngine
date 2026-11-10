using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Player;
using VoxelEngine.Rendering;
using VoxelEngine.UI.Menus;
using Xunit;

namespace VoxelEngine.Tests.Rendering;

/// <summary>
/// <see cref="ShadowCasterCull"/> lets the shadow passes skip chunk and far-LOD casters, and the skip
/// must be exact: a caster may only be dropped when no texel it could write is read by any shadow
/// sample this frame. These facts drive the rule with receivers sampled straight from the views'
/// clip volumes (never from the helper's own corner math) and a C# oracle of the shaders' sampling,
/// so a reach term left out, a missing march origin or a missing mirror shows up as a dropped caster
/// under a sampled texel.
/// </summary>
public sealed class ShadowCasterCullTests
{
    public ShadowCasterCullTests() => TestContent.EnsureLoaded();

    private static readonly bool NearPcf = LightingConfig.ShadowSoftnessTexels > 0f;

    /// <summary>Each cascade: radius, map size and whether it runs the four-tap PCF kernel.</summary>
    private static readonly (float Radius, int Size, bool Pcf)[] Cascades =
    [
        (LightingConfig.ShadowRadiusBlocks, LightingConfig.ShadowMapSize, NearPcf),
        (LightingConfig.FarShadowRadiusBlocks, LightingConfig.FarShadowMapSize, false),
    ];

    private static readonly Vector3[] Suns =
        [ShadowCenterTests.SunAt(1.2f), ShadowCenterTests.SunAt(2.4f), ShadowCenterTests.SunAt(0.4f), ShadowCenterTests.SunAt(0.12f)];

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

    private static Matrix4x4 Projection(float fovDegrees, float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(fovDegrees * MathF.PI / 180f, aspect, 0.2f, Camera.FarPlane);

    private static ShadowCascade Cascade(float radius, int size, Vector3 eye, Vector3 dir, Vector3 sun)
    {
        var cascade = new ShadowCascade(radius, size);
        cascade.Update(eye, dir, sun);
        return cascade;
    }

    private static bool InClip(Vector3 p, in Matrix4x4 viewProj)
    {
        var c = Vector4.Transform(new Vector4(p, 1f), viewProj);
        return c.W > 0f && MathF.Abs(c.X) <= c.W && MathF.Abs(c.Y) <= c.W && c.Z >= 0f && c.Z <= c.W;
    }

    private static float Range(Random rng, float min, float max) => min + (float)rng.NextDouble() * (max - min);

    private static Vector3 InCube(Random rng, Vector3 center, float half) =>
        center + new Vector3(Range(rng, -half, half), Range(rng, -half, half), Range(rng, -half, half));

    private static Vector3 RandomUnit(Random rng)
    {
        while (true)
        {
            var v = new Vector3(Range(rng, -1f, 1f), Range(rng, -1f, 1f), Range(rng, -1f, 1f));
            var len = v.Length();
            if (len > 0.1f && len <= 1f) return v / len;
        }
    }

    /// <summary>
    /// Points the view really draws: random points in cubes around <paramref name="eye"/> (from under
    /// a block across, where the pyramid's apex is, up to the cascade's own size), kept only when
    /// they land inside the clip volume.
    /// </summary>
    private static List<Vector3> Receivers(Random rng, Vector3 eye, in Matrix4x4 viewProj, float radius, int count)
    {
        var halfSizes = new[] { 0.4f, 1f, 6f, radius * 0.25f, radius };
        var points = new List<Vector3>(count);
        for (var attempt = 0; points.Count < count && attempt < count * 400; attempt++)
        {
            var half = halfSizes[attempt % halfSizes.Length];
            var p = InCube(rng, eye, half);
            if (!InClip(p, viewProj)) continue;

            // Every other receiver is moved onto the clip volume's boundary: bisect toward a point the
            // view does not draw, keeping the drawn end, so the samples reach the hull's very edge.
            if (points.Count % 2 == 1)
            {
                var outside = InCube(rng, eye, half);
                if (InClip(outside, viewProj)) continue;
                for (var i = 0; i < 40; i++)
                {
                    var mid = (p + outside) * 0.5f;
                    if (InClip(mid, viewProj)) p = mid;
                    else outside = mid;
                }
            }

            points.Add(p);
        }

        Assert.True(points.Count == count, "rejection sampling found too few receivers");
        return points;
    }

    /// <summary>
    /// A C# oracle of <c>test-fragment.glsl</c>'s <c>CascadeLookup</c> + <c>SampleShadow</c>: the
    /// normal offset, the projection, the near cascade's four diagonal taps and the hardware 2x2
    /// bilinear compare of each. Every texel center a sample touches, mapped back to the world and
    /// slid along the sun to any depth, must be kept as a caster point.
    /// </summary>
    private static void AssertSamplesKept(ShadowCasterCull cull, ShadowCascade cascade, bool pcf,
        IEnumerable<Vector3> receivers, Random rng, Vector3 sun, string context)
    {
        var vp = cascade.ViewProj;
        Assert.True(Matrix4x4.Invert(vp, out var inverse));
        var size = cascade.MapSize;
        var o = 0.5f * LightingConfig.ShadowSoftnessTexels / size;
        Span<Vector2> taps = stackalloc Vector2[4];

        foreach (var p in receivers)
        {
            // Half the samples take the worst case: a normal square to the sun (ndl 0), whose whole
            // offset is lateral in light space.
            var n = RandomUnit(rng);
            var ndl = (float)rng.NextDouble();
            if (rng.Next(2) == 0)
            {
                n = Vector3.Normalize(n - sun * Vector3.Dot(n, sun));
                ndl = 0f;
            }
            var q = p + n * (cascade.WorldTexel * LightingConfig.ShadowNormalOffsetTexels * (1f - ndl));
            var lp = Vector4.Transform(new Vector4(q, 1f), vp);
            var uv = new Vector2(lp.X * 0.5f + 0.5f, lp.Y * 0.5f + 0.5f);

            var tapCount = 1;
            taps[0] = uv;
            if (pcf)
            {
                taps[0] = uv + new Vector2(-o, -o);
                taps[1] = uv + new Vector2(o, -o);
                taps[2] = uv + new Vector2(-o, o);
                taps[3] = uv + new Vector2(o, o);
                tapCount = 4;
            }

            for (var t = 0; t < tapCount; t++)
            {
                var ix = (int)MathF.Floor(taps[t].X * size - 0.5f);
                var iy = (int)MathF.Floor(taps[t].Y * size - 0.5f);
                for (var dx = 0; dx <= 1; dx++)
                for (var dy = 0; dy <= 1; dy++)
                {
                    var ndcX = (ix + dx + 0.5f) / size * 2f - 1f;
                    var ndcY = (iy + dy + 0.5f) / size * 2f - 1f;
                    var world = Vector4.Transform(new Vector4(ndcX, ndcY, lp.Z, 1f), inverse);
                    var texelPoint = new Vector3(world.X, world.Y, world.Z) / world.W
                                     + sun * Range(rng, -2f * cascade.RadiusBlocks, 2f * cascade.RadiusBlocks);
                    Assert.True(cull.MayShadowReceivers(texelPoint, texelPoint),
                        $"{context}: a texel sampled for receiver {p} was culled");
                }
            }
        }
    }

    [Fact]
    public void EverySampledTexelOfAReceiverIsKept()
    {
        var rng = new Random(1234);
        var cull = new ShadowCasterCull();
        foreach (var pose in Poses)
        foreach (var sun in Suns)
        foreach (var (radius, size, pcf) in Cascades)
        {
            var dir = ViewDir(pose.Yaw, pose.Pitch);
            var view = View(pose.Eye, dir, pose.Roll);
            var projection = Projection(pose.Fov, pose.Aspect);
            var cascade = Cascade(radius, size, pose.Eye, dir, sun);

            cull.Begin(cascade.ViewProj, size, ShadowCasterCull.ReceiverReachTexels(pcf), casterReachBlocks: 0f);
            cull.AddViewPyramid(view, projection, Camera.FarPlane, pose.Eye);

            var receivers = Receivers(rng, pose.Eye, view * projection, radius, 400);
            AssertSamplesKept(cull, cascade, pcf, receivers, rng, sun, $"pose {pose.Eye}, cascade {radius}");
        }
    }

    [Fact]
    public void TheMarchesSamplesAreKept()
    {
        var rng = new Random(77);
        var cull = new ShadowCasterCull();
        foreach (var pose in Poses)
        foreach (var sun in Suns)
        foreach (var (radius, size, _) in Cascades)
        {
            var dir = ViewDir(pose.Yaw, pose.Pitch);
            var view = View(pose.Eye, dir, pose.Roll);
            var projection = Projection(pose.Fov, pose.Aspect);
            var cascade = Cascade(radius, size, pose.Eye, dir, sun);
            // The march origin is the render eye, a little off the view matrix's eye (bob, shake),
            // here behind it, outside the view's own pyramid.
            var renderEye = pose.Eye - dir * Range(rng, 0.2f, 0.3f) + RandomUnit(rng) * 0.05f;

            // Only the float slack as reach: the march samples its points with no offset or kernel,
            // so the receiver hull itself must hold every point of the march.
            cull.Begin(cascade.ViewProj, size, ShadowCasterCull.SafetyTexels, casterReachBlocks: 0f);
            cull.AddViewPyramid(view, projection, 2f * Camera.FarPlane, renderEye);

            foreach (var target in Receivers(rng, pose.Eye, view * projection, radius, 120))
            for (var s = 0f; s <= 1f; s += 0.125f)
            {
                var p = renderEye + (target - renderEye) * s
                        + sun * Range(rng, -2f * radius, 2f * radius);
                Assert.True(cull.MayShadowReceivers(p, p),
                    $"march point {p} toward {target} from {renderEye} was culled (cascade {radius})");
            }
        }
    }

    [Fact]
    public void EverySampleOfTheMirrorIsKept()
    {
        var rng = new Random(4242);
        var cull = new ShadowCasterCull();
        var plane = RenderManager.WaterPlaneY;
        foreach (var pose in Poses)
        foreach (var sun in Suns)
        foreach (var (radius, size, pcf) in Cascades)
        {
            var eye = new Vector3(pose.Eye.X, plane + MathF.Abs(pose.Eye.Y - plane) % 40f + 0.5f, pose.Eye.Z);
            var dir = ViewDir(pose.Yaw, pose.Pitch);
            var mirrorView = ReflectionCull.MirrorView(eye, dir, plane, out var mirroredEye, out _);
            var projection = Projection(pose.Fov, pose.Aspect);
            var cascade = Cascade(radius, size, eye, dir, sun);

            cull.Begin(cascade.ViewProj, size, ShadowCasterCull.ReceiverReachTexels(pcf), casterReachBlocks: 0f);
            cull.AddViewPyramid(mirrorView, projection, 2f * Camera.FarPlane, mirroredEye);

            var receivers = Receivers(rng, mirroredEye, mirrorView * projection, radius, 300);
            AssertSamplesKept(cull, cascade, pcf, receivers, rng, sun, $"mirror of {eye}, cascade {radius}");
        }
    }

    /// <summary>A high sun, a camera looking along +X, and a chunk-sized box well behind it.</summary>
    private static (ShadowCascade Cascade, Matrix4x4 View, Matrix4x4 Projection, Vector3 Eye, Vector3 Min, Vector3 Max)
        BehindTheCamera()
    {
        var eye = new Vector3(8f, 80f, 8f);
        var sun = ShadowCenterTests.SunAt(MathF.PI / 2f - 0.1f);
        var view = Matrix4x4.CreateLookAt(eye, eye + Vector3.UnitX, Vector3.UnitY);
        var projection = Projection(70f, 16f / 9f);
        var cascade = Cascade(LightingConfig.ShadowRadiusBlocks, LightingConfig.ShadowMapSize, eye, Vector3.UnitX, sun);
        var min = eye + new Vector3(-48f, -8f, -8f);
        return (cascade, view, projection, eye, min, min + new Vector3(Chunk.Size));
    }

    private static void BeginReal(ShadowCasterCull cull, ShadowCascade cascade) =>
        cull.Begin(cascade.ViewProj, cascade.MapSize, ShadowCasterCull.ReceiverReachTexels(NearPcf),
            ShadowCasterCull.CasterReachBlocks);

    [Fact]
    public void ACasterBehindTheCameraUnderAHighSunIsDropped()
    {
        var (cascade, view, projection, eye, min, max) = BehindTheCamera();
        var cull = new ShadowCasterCull();

        BeginReal(cull, cascade);
        cull.AddViewPyramid(view, projection, 2f * Camera.FarPlane, eye);
        Assert.False(cull.MayShadowReceivers(min, max), "a caster behind the camera under a high sun was kept");

        // A second view looking back at it brings it into the union.
        var back = Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitX, Vector3.UnitY);
        cull.AddViewPyramid(back, projection, 2f * Camera.FarPlane, eye);
        Assert.True(cull.MayShadowReceivers(min, max), "a caster in front of the second view was dropped");
    }

    [Fact]
    public void AWiderReachNeverDropsAKeptCaster()
    {
        var rng = new Random(99);
        var narrow = new ShadowCasterCull();
        var wide = new ShadowCasterCull();
        var dropped = 0;
        var kept = 0;
        foreach (var pose in Poses)
        foreach (var sun in Suns)
        foreach (var (radius, size, _) in Cascades)
        {
            var dir = ViewDir(pose.Yaw, pose.Pitch);
            var view = View(pose.Eye, dir, pose.Roll);
            var projection = Projection(pose.Fov, pose.Aspect);
            var cascade = Cascade(radius, size, pose.Eye, dir, sun);

            for (var i = 0; i < 60; i++)
            {
                var receiverReach = Range(rng, 0f, 6f);
                var casterReach = Range(rng, 0f, 3f);
                narrow.Begin(cascade.ViewProj, size, receiverReach, casterReach);
                wide.Begin(cascade.ViewProj, size, receiverReach + Range(rng, 0f, 4f), casterReach + Range(rng, 0f, 2f));
                narrow.AddViewPyramid(view, projection, 2f * Camera.FarPlane, pose.Eye);
                wide.AddViewPyramid(view, projection, 2f * Camera.FarPlane, pose.Eye);

                var center = pose.Eye + new Vector3(Range(rng, -radius, radius), Range(rng, -radius, radius),
                    Range(rng, -radius, radius));
                var half = new Vector3(Range(rng, 0f, 16f), Range(rng, 0f, 16f), Range(rng, 0f, 16f));
                if (!narrow.MayShadowReceivers(center - half, center + half))
                {
                    dropped++;
                    continue;
                }

                kept++;
                Assert.True(wide.MayShadowReceivers(center - half, center + half),
                    $"a caster at {center} kept with the narrower reach was dropped with the wider one");
            }
        }

        Assert.True(dropped > 0 && kept > 0, "the sweep never both kept and dropped a caster");
    }

    [Fact]
    public void WithoutAReceiverRegionEveryCasterIsKept()
    {
        var (cascade, view, projection, eye, min, max) = BehindTheCamera();
        var cull = new ShadowCasterCull();

        BeginReal(cull, cascade);
        Assert.True(cull.MayShadowReceivers(min, max), "with no receiver pyramid a caster was dropped");

        cull.AddViewPyramid(view, projection, 2f * Camera.FarPlane, eye);
        Assert.False(cull.MayShadowReceivers(min, max), "the camera pyramid did not cull the caster behind it");

        cull.KeepAll();
        Assert.True(cull.MayShadowReceivers(min, max), "KeepAll still dropped a caster");
    }

    [Fact]
    public void ABeginClearsADegeneratePyramid()
    {
        var (cascade, view, projection, eye, min, max) = BehindTheCamera();
        var cull = new ShadowCasterCull();

        BeginReal(cull, cascade);
        var nanEye = new Vector3(float.NaN, eye.Y, eye.Z);
        cull.AddViewPyramid(Matrix4x4.CreateLookAt(nanEye, nanEye + Vector3.UnitX, Vector3.UnitY), projection,
            2f * Camera.FarPlane, nanEye);
        Assert.True(cull.MayShadowReceivers(min, max), "a non-finite pyramid did not keep every caster");

        BeginReal(cull, cascade);
        cull.AddViewPyramid(view, projection, 2f * Camera.FarPlane, eye);
        Assert.False(cull.MayShadowReceivers(min, max), "the keep-all of the previous frame outlived Begin");
    }

    [Fact]
    public void ANearlyFlatPyramidNeverOverflowsAHull()
    {
        // A pyramid whose light-space projection is nearly a line (its points collinear up to float
        // noise) can make the monotone chain keep more vertices than there are points; both hulls
        // must still build, and each view's own eye must stay kept.
        var rng = new Random(11);
        var cull = new ShadowCasterCull();
        var projection = Projection(70f, 16f / 9f);
        for (var trial = 0; trial < 20000; trial++)
        {
            var row = RandomUnit(rng) * Range(rng, 0.001f, 1f);
            var lean = Range(rng, -2f, 2f);
            var noise = RandomUnit(rng) * Range(rng, 0f, 1e-6f);
            var light = new Matrix4x4(
                row.X, row.X * lean + noise.X, 0f, 0f,
                row.Y, row.Y * lean + noise.Y, 0f, 0f,
                row.Z, row.Z * lean + noise.Z, 0f, 0f,
                Range(rng, -1f, 1f), Range(rng, -1f, 1f), 0f, 1f);
            cull.Begin(light, 2048, 1f, 0f);
            for (var hull = 0; hull < 2; hull++)
            {
                var eye = InCube(rng, Vector3.Zero, 3000f);
                var dir = RandomUnit(rng);
                var up = MathF.Abs(dir.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
                cull.AddViewPyramid(Matrix4x4.CreateLookAt(eye, eye + dir, up), projection, 2f * Camera.FarPlane, eye);
                Assert.True(cull.MayShadowReceivers(eye, eye), "a flat pyramid dropped a caster at its own eye");
            }
        }
    }

    [Fact]
    public void TheSwayDisplacementNeverExceedsTheSwayReach()
    {
        // A C# replica of shadow-vertex.glsl's sway displacement (byte-identical to test-vertex.glsl's
        // sway block). Keep it in step with the shader, see the shader-sync skill.
        const float reach = ShadowCasterCull.SwayReachBlocks;
        var steps = new[] { -1f, -0.7f, -0.3f, 0f, 0.4f, 0.8f, 1f };
        var gusts = new[] { 0.2f, 0.6f, 1f };
        var weights = new[] { 0f, 0.5f, 1f };
        var strengths = new[] { 0f, 1f, SettingsRows.LeafSwayMax };
        for (var angle = 0f; angle < MathF.Tau; angle += 0.4f)
        {
            var wind = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * SkyConfig.WindLean;
            foreach (var w1 in steps)
            foreach (var w2 in steps)
            foreach (var gust in gusts)
            foreach (var sway in weights)
            foreach (var strength in strengths)
            {
                var scale = sway * strength * gust;
                var d = (new Vector3(w1 * 0.055f + w2 * 0.028f, (w1 + w2) * 0.018f, w2 * 0.055f - w1 * 0.028f)
                         + new Vector3(wind.X, 0f, wind.Y)) * scale;
                Assert.True(MathF.Abs(d.X) <= reach && MathF.Abs(d.Y) <= reach && MathF.Abs(d.Z) <= reach,
                    $"sway {d} passes the sway reach {reach}");
            }
        }
    }

    [Fact]
    public void NoBakedModelVertexReachesPastTheOverhangOnAnyAxis()
    {
        const float low = -ReflectionCull.MeshOverhangBlocks;
        const float high = 1f + ReflectionCull.MeshOverhangBlocks;
        var quads = 0;
        foreach (var block in BlockType.ById)
        {
            if (block == null) continue;
            if (block.BakedModel != null) quads += Check(block, block.BakedModel);
            if (block.RotatedModels == null) continue;
            foreach (var model in block.RotatedModels) quads += Check(block, model);
        }

        Assert.True(quads > 0, "no baked quad was checked");
        return;

        int Check(BlockType block, VoxelEngine.Content.Models.BakedModel model)
        {
            foreach (var quad in model.Quads)
            foreach (var corner in quad.Positions)
                Assert.True(corner.X >= low && corner.X <= high && corner.Y >= low && corner.Y <= high
                            && corner.Z >= low && corner.Z <= high,
                    $"block '{block.Name}' has a vertex at {corner}, past the overhang [{low}, {high}]");
            return model.Quads.Length;
        }
    }

    [Fact]
    public void EveryOpaqueChunkVertexStaysWithinTheOverhangOfItsChunk()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        const int last = Chunk.Size - 1;

        // The shell of the chunk, the cells on its +X/+Y edge first (a multiblock anchor there pokes
        // past the chunk), then the rest of the +X/+Y/+Z faces, then the -X/-Y/-Z faces.
        var cells = new List<(int X, int Y, int Z)>();
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            if (x is 0 or last || y is 0 or last || z is 0 or last)
                cells.Add((x, y, z));
        cells = cells
            .OrderBy(c => c.X == last && c.Y == last ? 0 : c.X == last || c.Y == last || c.Z == last ? 1 : 2)
            .ToList();

        var blocks = BlockType.ById
            .Where(b => b != null && b.Id != 0 && b.Renderer != BlockRenderer.MultiblockPart)
            .Select(b => b!)
            .OrderBy(b => b.Renderer == BlockRenderer.Multiblock ? 0 : b.RotatedModels != null ? 1 : 2)
            .ToList();
        Assert.True(blocks.Count <= cells.Count, "more blocks than shell cells");
        Assert.Contains(blocks, b => b.Renderer == BlockRenderer.Multiblock);

        for (var i = 0; i < blocks.Count; i++)
            center.SetBlockRaw(cells[i].X, cells[i].Y, cells[i].Z, blocks[i].Id, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.NotEmpty(mesh.Vertices);

        const float low = -ReflectionCull.MeshOverhangBlocks;
        const float high = Chunk.Size + ReflectionCull.MeshOverhangBlocks;
        foreach (var vertex in mesh.Vertices)
        {
            var p = vertex.Position;
            Assert.True(p.X >= low && p.X <= high && p.Y >= low && p.Y <= high && p.Z >= low && p.Z <= high,
                $"an opaque vertex at {p} reaches past the chunk's overhang [{low}, {high}]");
        }
    }
}
