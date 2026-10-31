using System;
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
        var planes = new[] { margin * 0.5f, 1f, RenderManager.WaterPlaneY, margin, 2f * margin };
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
}
