using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Shapes;

namespace VoxelEngine.Interaction;

/// <summary>
/// GL-free: the outline segments of the union of a block's hitboxes. A block built from several
/// overlapping or touching boxes (a tree segment, a hollow log) outlines as one shape: only the
/// edges where the union's surface actually turns a corner are kept, so the seams where one box
/// meets or sinks into another get no line. Each box edge is cut at every box boundary along its
/// axis, a piece is kept when the four cells around it hold one, three, or two diagonal boxes'
/// worth of solid (a flat surface or open air has no edge), and collinear pieces are merged.
/// </summary>
public static class HitboxUnionEdges
{
    private const float Probe = 1e-4f;
    private const float Merge = 1e-5f;

    /// <summary>A straight outline segment along one axis, in the same space as the boxes.</summary>
    public readonly record struct Segment(Vector3 From, Vector3 To);

    /// <summary>
    /// Replaces <paramref name="result"/> with the union's edge segments for <paramref name="boxes"/>,
    /// each grown by <paramref name="pad"/> in total on every axis.
    /// </summary>
    public static void Build(IReadOnlyList<BlockHitbox> boxes, float pad, List<Segment> result)
    {
        result.Clear();
        var grown = new (float[] Min, float[] Max)[boxes.Count];
        var cuts = new List<float>[] { [], [], [] };
        for (var i = 0; i < boxes.Count; i++)
        {
            var half = pad * 0.5f;
            grown[i] = (Components(boxes[i].Min - new Vector3(half)), Components(boxes[i].Max + new Vector3(half)));
            for (var a = 0; a < 3; a++)
            {
                cuts[a].Add(grown[i].Min[a]);
                cuts[a].Add(grown[i].Max[a]);
            }
        }
        foreach (var c in cuts) c.Sort();

        var pieces = new List<(int Axis, float A, float B, float From, float To)>();
        foreach (var (min, max) in grown)
        for (var axis = 0; axis < 3; axis++)
        {
            int pa = (axis + 1) % 3, pb = (axis + 2) % 3;
            for (var u = 0; u < 2; u++)
            for (var v = 0; v < 2; v++)
            {
                var a = u == 0 ? min[pa] : max[pa];
                var b = v == 0 ? min[pb] : max[pb];
                AddEdgePieces(grown, cuts[axis], axis, pa, pb, a, b, min[axis], max[axis], pieces);
            }
        }

        pieces.Sort((x, y) =>
        {
            var c = x.Axis.CompareTo(y.Axis);
            if (c != 0) return c;
            c = x.A.CompareTo(y.A);
            if (c != 0) return c;
            c = x.B.CompareTo(y.B);
            return c != 0 ? c : x.From.CompareTo(y.From);
        });

        for (var i = 0; i < pieces.Count; i++)
        {
            var (axis, a, b, from, to) = pieces[i];
            // Absorb every following piece on the same line that touches or overlaps this one
            while (i + 1 < pieces.Count && pieces[i + 1].Axis == axis
                   && MathF.Abs(pieces[i + 1].A - a) <= Merge && MathF.Abs(pieces[i + 1].B - b) <= Merge
                   && pieces[i + 1].From <= to + Merge)
            {
                to = MathF.Max(to, pieces[i + 1].To);
                i++;
            }
            result.Add(MakeSegment(axis, a, b, from, to));
        }
    }

    private static void AddEdgePieces((float[] Min, float[] Max)[] boxes, List<float> cuts, int axis, int pa, int pb,
        float a, float b, float lo, float hi, List<(int, float, float, float, float)> pieces)
    {
        var from = lo;
        foreach (var cut in cuts)
        {
            if (cut <= from + Merge) continue;
            var to = MathF.Min(cut, hi);
            if (IsCorner(boxes, axis, pa, pb, a, b, (from + to) * 0.5f))
                pieces.Add((axis, a, b, from, to));
            from = to;
            if (from >= hi - Merge) break;
        }
    }

    /// <summary>
    /// True when the point on the edge line has solid in exactly one or three of the four cells
    /// around it, or in two diagonal ones: the cases where the surface turns.
    /// </summary>
    private static bool IsCorner((float[] Min, float[] Max)[] boxes, int axis, int pa, int pb, float a, float b,
        float along)
    {
        var solid = new bool[4];
        var count = 0;
        for (var q = 0; q < 4; q++)
        {
            var p = new float[3];
            p[axis] = along;
            p[pa] = a + ((q & 1) == 0 ? -Probe : Probe);
            p[pb] = b + ((q & 2) == 0 ? -Probe : Probe);
            solid[q] = Contains(boxes, p);
            if (solid[q]) count++;
        }
        return count is 1 or 3 || (count == 2 && solid[0] == solid[3]);
    }

    private static bool Contains((float[] Min, float[] Max)[] boxes, float[] p)
    {
        foreach (var (min, max) in boxes)
        {
            if (p[0] > min[0] && p[0] < max[0] && p[1] > min[1] && p[1] < max[1] && p[2] > min[2] && p[2] < max[2])
                return true;
        }
        return false;
    }

    private static Segment MakeSegment(int axis, float a, float b, float from, float to)
    {
        var p = new float[3];
        p[(axis + 1) % 3] = a;
        p[(axis + 2) % 3] = b;
        p[axis] = from;
        var start = new Vector3(p[0], p[1], p[2]);
        p[axis] = to;
        return new Segment(start, new Vector3(p[0], p[1], p[2]));
    }

    private static float[] Components(Vector3 v) => [v.X, v.Y, v.Z];
}
