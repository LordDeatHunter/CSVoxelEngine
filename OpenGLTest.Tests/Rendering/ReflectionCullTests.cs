using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Rendering;
using Xunit;

namespace VoxelEngine.Tests.Rendering;

/// <summary>
/// <see cref="ReflectionCull"/> is the GL-free rule the water mirror uses to skip opaque chunks and
/// far-LOD regions that lie wholly under its clip plane. It must be conservative: anything whose
/// fragments could reach the plane is kept, or the mirror would lose geometry.
/// </summary>
public sealed class ReflectionCullTests
{
    public ReflectionCullTests() => TestContent.EnsureLoaded();

    [Fact]
    public void AGeometryTopThatCanReachThePlaneIsKept()
    {
        const float margin = ReflectionCull.TopMarginBlocks;
        // Every plane lies in [margin / 2, 2 * margin], so plane - margin is exact (Sterbenz) and a
        // top exactly one margin below the plane adds back up to the plane itself.
        var planes = new[] { margin * 0.5f, 1f, RenderManager.WaterPlaneY, margin, 2f * margin }
            .Where(plane => plane >= margin * 0.5f && plane <= 2f * margin);
        const float step = 0.05f;
        foreach (var plane in planes)
        {
            var lowestReach = plane - margin;
            Assert.True(ReflectionCull.MayReachPlane(lowestReach, plane),
                $"a top one margin below plane {plane} must be kept");

            var keptBefore = false;
            for (var top = plane - 4f * margin; top <= plane + 2f * margin; top += step)
            {
                var kept = ReflectionCull.MayReachPlane(top, plane);
                if (top >= lowestReach) Assert.True(kept, $"top {top} can reach plane {plane}");
                if (top < lowestReach - 1e-3f) Assert.False(kept, $"top {top} cannot reach plane {plane}");
                // Monotone: once a top is kept, every higher top is kept too.
                if (keptBefore) Assert.True(kept, $"top {top} dropped above a kept top (plane {plane})");
                keptBefore |= kept;
            }

            Assert.True(keptBefore, $"the sweep around plane {plane} kept nothing");
        }
    }

    [Fact]
    public void NoBakedModelReachesPastTheOverhang()
    {
        const float ceiling = 1f + ReflectionCull.MeshOverhangBlocks;
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
                Assert.True(corner.Y <= ceiling,
                    $"block '{block.Name}' has a vertex at y {corner.Y}, above the overhang ceiling {ceiling}");
            return model.Quads.Length;
        }
    }

    [Fact]
    public void ChunkOverTheSeaIsKeptAndDeepChunksAreDropped()
    {
        var plane = RenderManager.WaterPlaneY;
        Assert.True(plane >= WorldGenerationConfig.SeaLevel, "the mirror plane sits on the sea surface");

        // The top of the highest chunk layer lying wholly at or below the plane.
        var floorTop = MathF.Floor(plane / Chunk.Size) * Chunk.Size;

        // The chunk holding the plane always draws into the mirror.
        Assert.True(ReflectionCull.MayReachPlane(floorTop + Chunk.Size));

        // The layer right under the plane: a multiblock on its top row pokes one block up, so it is
        // kept whenever that reaches the plane (always, while the sea surface sits less than a block
        // above a chunk boundary).
        if (plane - floorTop <= ReflectionCull.MeshOverhangBlocks)
            Assert.True(ReflectionCull.MayReachPlane(floorTop));

        // A full chunk lower nothing can reach the plane: the margin is far under a chunk's height.
        Assert.False(ReflectionCull.MayReachPlane(floorTop - Chunk.Size));
        Assert.False(ReflectionCull.MayReachPlane(floorTop - 4 * Chunk.Size));
    }

    [Fact]
    public void TheMirrorMayRenderOnlyWhenReflectionsAreOnAndTheMirrorApplies()
    {
        var plane = RenderManager.WaterPlaneY;
        var sawTrue = false;
        var sawFalseWhileOn = false;
        for (var eyeY = plane - 3f; eyeY <= plane + 3f; eyeY += 0.25f)
        foreach (var inWater in new[] { false, true })
        {
            Assert.False(ReflectionCull.MirrorMayRender(false, eyeY, inWater, plane),
                $"reflections off must never render the mirror (eye y {eyeY}, in water {inWater})");

            var expected = ReflectionReuse.MirrorApplies(eyeY, plane, inWater);
            var gate = ReflectionCull.MirrorMayRender(true, eyeY, inWater, plane);
            Assert.Equal(expected, gate);
            sawTrue |= gate;
            sawFalseWhileOn |= !gate;
        }

        Assert.True(sawTrue, "the sweep never let the mirror render");
        Assert.True(sawFalseWhileOn, "the sweep never refused the mirror with reflections on");
    }

    [Fact]
    public void TheMirrorViewLooksFromTheReflectedEyeAlongTheReflectedView()
    {
        var plane = RenderManager.WaterPlaneY;
        var eyes = new[]
        {
            new Vector3(0f, plane + 5f, 0f),
            new Vector3(-40f, plane + 0.5f, 120f),
            new Vector3(300f, plane + 60f, -75f),
        };
        var dirs = new[]
        {
            Vector3.Normalize(new Vector3(1f, -0.3f, 0f)),
            Vector3.Normalize(new Vector3(-0.2f, 0.4f, -1f)),
            Vector3.Normalize(new Vector3(0.6f, -0.9f, 0.5f)),
            -Vector3.UnitZ,
        };

        foreach (var eye in eyes)
        foreach (var dir in dirs)
        {
            var view = ReflectionCull.MirrorView(eye, dir, plane, out var mirroredEye, out var mirroredForward);

            // The mirrored eye sits as far from the plane as the eye, on the other side, straight under it.
            Assert.Equal(eye.Y - plane, plane - mirroredEye.Y, 3);
            Assert.Equal(eye.X, mirroredEye.X);
            Assert.Equal(eye.Z, mirroredEye.Z);

            // The mirrored forward is the view direction reflected across the plane.
            Assert.Equal(dir.Y, -mirroredForward.Y);

            // A point ahead of the eye, reflected across the plane, lies straight ahead of the mirror view.
            var ahead = eye + dir * 10f;
            var reflected = new Vector3(ahead.X, 2f * plane - ahead.Y, ahead.Z);
            var inView = Vector3.Transform(reflected, view);
            Assert.True(MathF.Abs(inView.X) < 1e-3f, $"x {inView.X} off the mirror's view axis");
            Assert.True(MathF.Abs(inView.Y) < 1e-3f, $"y {inView.Y} off the mirror's view axis");
            Assert.True(inView.Z < 0f, $"the reflected point (view z {inView.Z}) is not in front of the mirror");
        }
    }
}
