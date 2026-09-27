using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class SelectionOutlineMeshTests
{
    private const float Eps = 1e-5f;

    public static IEnumerable<object[]> Boxes()
    {
        // A full cube, a thin slab and an offset, non-cubic box.
        yield return [Vector3.Zero, Vector3.One, 0.02f];
        yield return [Vector3.Zero, new Vector3(1f, 0.25f, 1f), 0.02f];
        yield return [new Vector3(3.2f, -1f, 7.5f), new Vector3(3.7f, 0.5f, 8f), 0.05f];
    }

    private static Vector3[] Vertices(Vector3 min, Vector3 max, float t)
    {
        var v = new Vector3[SelectionOutlineMesh.VertexCount];
        SelectionOutlineMesh.BuildVertices(min, max, t, v);
        return v;
    }

    private static uint[] Indices()
    {
        var i = new uint[SelectionOutlineMesh.IndexCount];
        SelectionOutlineMesh.BuildIndices(i);
        return i;
    }

    private static float Axis(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;

    /// <summary>The beams as (lo, hi) boxes: each group of 8 vertices spans one.</summary>
    private static List<(Vector3 Lo, Vector3 Hi)> Beams(Vector3[] verts)
    {
        var beams = new List<(Vector3, Vector3)>();
        for (var b = 0; b < verts.Length; b += 8)
        {
            var group = verts.Skip(b).Take(8).ToArray();
            beams.Add((group.Aggregate(Vector3.Min), group.Aggregate(Vector3.Max)));
        }
        return beams;
    }

    private static IEnumerable<Vector3> EdgeMidpoints(Vector3 min, Vector3 max)
    {
        var mid = (min + max) * 0.5f;
        for (var u = 0; u < 2; u++)
        for (var v = 0; v < 2; v++)
        {
            float x = u == 0 ? min.X : max.X, y = u == 0 ? min.Y : max.Y;
            float y2 = v == 0 ? min.Y : max.Y, z = v == 0 ? min.Z : max.Z;
            yield return new Vector3(mid.X, y, z);  // along X
            yield return new Vector3(x, mid.Y, z);  // along Y
            yield return new Vector3(x, y2, mid.Z); // along Z
        }
    }

    private static bool Inside(Vector3 p, (Vector3 Lo, Vector3 Hi) b) =>
        p.X >= b.Lo.X - Eps && p.X <= b.Hi.X + Eps &&
        p.Y >= b.Lo.Y - Eps && p.Y <= b.Hi.Y + Eps &&
        p.Z >= b.Lo.Z - Eps && p.Z <= b.Hi.Z + Eps;

    [Theory]
    [MemberData(nameof(Boxes))]
    public void IndicesAreInRangeAndTrianglesNonDegenerate(Vector3 min, Vector3 max, float t)
    {
        var verts = Vertices(min, max, t);
        var idx = Indices();

        Assert.Equal(0, idx.Length % 3);
        Assert.All(idx, i => Assert.InRange(i, 0u, (uint)(verts.Length - 1)));
        for (var k = 0; k < idx.Length; k += 3)
        {
            var a = verts[idx[k]];
            var area = Vector3.Cross(verts[idx[k + 1]] - a, verts[idx[k + 2]] - a).Length();
            Assert.True(area > 0f, $"triangle {k / 3} is degenerate");
        }
    }

    [Fact]
    public void TrianglesFaceOutOfTheirBeam()
    {
        var min = Vector3.Zero;
        var max = new Vector3(1f, 0.25f, 1f);
        var verts = Vertices(min, max, 0.02f);
        var idx = Indices();
        for (var k = 0; k < idx.Length; k += 3)
        {
            var beam = (int)idx[k] / 8;
            var group = verts.Skip(beam * 8).Take(8).ToArray();
            var beamCenter = (group.Aggregate(Vector3.Min) + group.Aggregate(Vector3.Max)) * 0.5f;
            var a = verts[idx[k]];
            var n = Vector3.Cross(verts[idx[k + 1]] - a, verts[idx[k + 2]] - a);
            var triCenter = (a + verts[idx[k + 1]] + verts[idx[k + 2]]) / 3f;
            Assert.True(Vector3.Dot(n, triCenter - beamCenter) > 0f, $"triangle {k / 3} faces inward");
        }
    }

    [Theory]
    [MemberData(nameof(Boxes))]
    public void VerticesSitOnEdgeBeamsOnly(Vector3 min, Vector3 max, float t)
    {
        var half = t * 0.5f;
        foreach (var p in Vertices(min, max, t))
        {
            // Near the boundary on at least two axes: every vertex belongs to an edge, not a face.
            var nearAxes = 0;
            var depthInside = float.MaxValue;
            var inside = true;
            for (var a = 0; a < 3; a++)
            {
                float c = Axis(p, a), lo = Axis(min, a), hi = Axis(max, a);
                if (MathF.Min(MathF.Abs(c - lo), MathF.Abs(c - hi)) <= half + Eps) nearAxes++;
                if (c < lo || c > hi) inside = false;
                else depthInside = MathF.Min(depthInside, MathF.Min(c - lo, hi - c));
            }
            Assert.True(nearAxes >= 2, $"vertex {p} is not on an edge beam");
            if (inside) Assert.True(depthInside <= half + Eps, $"vertex {p} sits deep inside the box");
        }
    }

    [Theory]
    [MemberData(nameof(Boxes))]
    public void EveryEdgeIsCovered(Vector3 min, Vector3 max, float t)
    {
        var beams = Beams(Vertices(min, max, t));
        var mids = EdgeMidpoints(min, max).ToList();
        Assert.Equal(12, mids.Distinct().Count());
        foreach (var m in mids)
            Assert.Contains(beams, b => Inside(m, b));

        // Corners are filled: every box corner lies inside three beams (one per axis).
        for (var c = 0; c < 8; c++)
        {
            var corner = new Vector3((c & 1) == 0 ? min.X : max.X, (c & 2) == 0 ? min.Y : max.Y,
                (c & 4) == 0 ? min.Z : max.Z);
            Assert.Equal(3, beams.Count(b => Inside(corner, b)));
        }
    }

    [Theory]
    [InlineData(0.002f)]
    [InlineData(0.02f)]
    [InlineData(0.1f)]
    [InlineData(0.5f)]
    public void ThinBoxesStillGetAllTwelveEdges(float t)
    {
        var min = new Vector3(0f, 0f, 0f);
        var max = new Vector3(1f, 0.0625f, 1f);
        var verts = Vertices(min, max, t);
        var beams = Beams(verts);
        foreach (var m in EdgeMidpoints(min, max))
            Assert.Contains(beams, b => Inside(m, b));
        // Each beam is exactly t across on its two cross axes, however thin the box.
        foreach (var (lo, hi) in beams)
        {
            var size = hi - lo;
            var across = new[] { size.X, size.Y, size.Z }.Count(s => MathF.Abs(s - t) <= Eps);
            Assert.True(across >= 2, $"beam {lo}..{hi} is not {t} across");
        }
    }
}
