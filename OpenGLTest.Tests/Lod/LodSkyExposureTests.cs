using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Data;
using VoxelEngine.Lod;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>
/// <see cref="LodSkyExposure"/> is the coarse skylight the far LOD shells bake into
/// <see cref="Vertex.Sun"/>. The rules pinned here: open sky (open ground, a straight wall, the
/// water sheet) keeps full sun so the LOD matches the near chunks, a rising occluder only ever
/// darkens, and a canopy bump is darker toward its base. Every assert is relational, so the
/// tuning constants can move freely.
/// </summary>
public sealed class LodSkyExposureTests
{
    public LodSkyExposureTests() => TestContent.EnsureLoaded();

    [Fact]
    public void ATopFaceWithEveryRingColumnBelowItIsFullyExposed()
    {
        // Ground at 10, no bump; the ring columns sit at or below it
        Assert.Equal(1f, LodSkyExposure.TopFace(10, 10, 10, 9, 3));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(30)]
    public void AStraightWallUnderOpenSkyKeepsFullSun(int top)
    {
        // A terrain face (cy <= ground) whose front columns all sit below the face: the coplanar
        // wall column is not an input at all, and the bump above ground must not matter
        const int ground = 10;
        for (var cy = 5; cy <= ground; cy++)
        for (var dy = 0; dy <= 1; dy++)
            Assert.Equal(1f, LodSkyExposure.SideFace(cy + dy, cy, ground, top, cy - 1, cy - 2, cy - 1));
    }

    [Fact]
    public void TheHorizonOnlyDarkensAsAnOccluderRisesAndThenSaturates()
    {
        const int vertexY = 10;
        var values = Enumerable.Range(0, 30).Select(a => LodSkyExposure.Horizon(vertexY, a, 0, 0)).ToList();

        for (var k = 1; k < values.Count; k++)
            Assert.True(values[k] <= values[k - 1], $"horizon rose at occluder top {k}");

        // Below the vertex the occluder blocks nothing
        for (var a = 0; a < vertexY; a++)
            Assert.Equal(values[0], values[a]);

        // Then one contiguous strictly decreasing run, then a plateau that is still shaded
        var first = Enumerable.Range(1, values.Count - 1).First(k => values[k] < values[k - 1]);
        var k2 = first;
        while (k2 < values.Count && values[k2] < values[k2 - 1]) k2++;
        Assert.True(k2 < values.Count, "the occluder never saturated within the sweep");
        for (var k = k2; k < values.Count; k++)
            Assert.Equal(values[k2 - 1], values[k]);
        Assert.True(values[^1] < values[0]);
    }

    [Fact]
    public void CombineStaysWithinZeroAndOne()
    {
        for (var h = 0f; h <= 1.5f; h += 0.05f)
        for (var u = 0f; u <= 1.5f; u += 0.05f)
        {
            var v = LodSkyExposure.Combine(h, u);
            Assert.InRange(v, float.Epsilon, 1f);
        }
    }

    [Fact]
    public void TheUnderstoryDarkensTowardTheBumpsBaseAndIsIgnoredBelowGround()
    {
        const int ground = 10;
        const int top = 18;
        var atTop = LodSkyExposure.Understory(top + 1, ground, top);
        var mid = LodSkyExposure.Understory((ground + top) / 2 + 1, ground, top);
        var atBase = LodSkyExposure.Understory(ground + 1, ground, top);
        Assert.Equal(1f, atTop);
        Assert.True(mid < atTop);
        Assert.True(atBase < mid);

        // A terrain side face below the ground line: two calls differing only in the bump agree
        Assert.Equal(
            LodSkyExposure.SideFace(8, 7, ground, ground, 9, 12, 6),
            LodSkyExposure.SideFace(8, 7, ground, top, 9, 12, 6));
    }

    [Fact]
    public void BuiltLodMeshesShadeCanopySidesAndKeepOpenSkyFullyLit()
    {
        const byte foliage = 253;
        const byte plain = 255;
        static byte Alpha(Vertex v) => (byte)(v.PackedColor >> 24);
        static bool IsTop(Vertex v) => (v.Normal & 7) == 4;

        LodMeshData? found = null;
        for (var rx = 0; rx <= 40 && found is null; rx++)
        for (var rz = 0; rz <= 40 && found is null; rz++)
        {
            var data = LodRegion.BuildMeshData(0, rx, rz);
            var hasSides = data.Vertices.Any(v => Alpha(v) == foliage && !IsTop(v));
            var hasTops = data.Vertices.Any(v => Alpha(v) == foliage && IsTop(v));
            if (hasSides && hasTops && data.WaterVertices.Count > 0)
                found = data;
        }

        Assert.NotNull(found);
        var mesh = found!;
        var foliageSides = mesh.Vertices.Where(v => Alpha(v) == foliage && !IsTop(v)).Select(v => v.Sun);
        var foliageTops = mesh.Vertices.Where(v => Alpha(v) == foliage && IsTop(v)).Select(v => v.Sun);
        Assert.True(foliageSides.Min() < foliageTops.Max(), "canopy sides are not darker than canopy tops");

        Assert.All(mesh.WaterVertices, v => Assert.Equal(byte.MaxValue, v.Sun));

        IEnumerable<Vertex> Plain(bool top) => mesh.Vertices.Where(v => Alpha(v) == plain && IsTop(v) == top);
        Assert.Contains(Plain(true), v => v.Sun == byte.MaxValue);
        Assert.Contains(Plain(false), v => v.Sun == byte.MaxValue);
    }
}
