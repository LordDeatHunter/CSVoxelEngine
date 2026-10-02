using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// The leaf rule, a pure function of a grown <see cref="TreeGraph"/> plus the obstacles of an
/// <see cref="ITreeSpace"/>: every <b>live tip</b> (a childless, non-feature node at or under the
/// species' <see cref="TreeSpecies.TipMaxPx"/>) carries a cluster of the species'
/// <see cref="TreeSpecies.LeafShape"/> and <see cref="TreeSpecies.LeafRadius"/>. The convention,
/// stated once: a leaf's stored hydration is <see cref="TreeSpecies.LeafHydration"/> minus the
/// Manhattan <see cref="Distance"/> to the nearest live tip whose cluster shape contains the
/// cell; a leaf is emitted only while that is at least 1, and 0 would decay. Phase 5's decay
/// manager re-derives the same number through <see cref="HydrationAt"/>.
/// </summary>
public static class LeafCluster
{
    /// <summary>Whether node <paramref name="i"/> is a live tip that grows a leaf cluster.</summary>
    public static bool IsLiveTip(TreeGraph graph, TreeSpecies species, int i)
    {
        ref readonly var n = ref graph[i];
        return !n.IsFeature && n.ChildCount == 0 && n.ThicknessPx <= species.TipMaxPx;
    }

    /// <summary>The hydration metric: the Manhattan length of an offset.</summary>
    public static int Distance(int dx, int dy, int dz) => Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz);

    /// <summary>
    /// Whether the offset from a tip lies inside its cluster. A blob is the ball of
    /// <paramref name="radius"/>; a cone hangs from the tip's level down over
    /// <paramref name="radius"/> levels, level <c>k</c> below the tip having horizontal radius
    /// <c>k + 1</c>, so it widens downward.
    /// </summary>
    public static bool InShape(LeafClusterShape shape, int radius, int dx, int dy, int dz)
    {
        if (shape == LeafClusterShape.Cone)
        {
            if (dy > 0 || dy <= -radius)
                return false;
            int r = 1 - dy;
            return dx * dx + dz * dz <= r * r;
        }
        return dx * dx + dy * dy + dz * dz <= radius * radius;
    }

    /// <summary>
    /// The hydration the rule gives the cell: the maximum over live tips whose shape holds it of
    /// <c>LeafHydration - Distance</c>, or 0 when no tip's shape holds it (callers test
    /// <c>&gt;= 1</c>). Ignores obstacles: it is the value a leaf standing there carries. No allocation.
    /// </summary>
    public static int HydrationAt(TreeGraph graph, TreeSpecies species, int x, int y, int z)
    {
        if (!species.HasLeaves)
            return 0;

        int best = 0;
        bool any = false;
        for (int i = 0; i < graph.Count; i++)
        {
            if (!IsLiveTip(graph, species, i))
                continue;
            ref readonly var t = ref graph[i];
            int dx = x - t.X, dy = y - t.Y, dz = z - t.Z;
            if (!InShape(species.LeafShape, species.LeafRadius, dx, dy, dz))
                continue;
            int h = species.LeafHydration - Distance(dx, dy, dz);
            if (!any || h > best)
            {
                best = h;
                any = true;
            }
        }
        return any ? best : 0;
    }

    /// <summary>
    /// Appends the tree's leaf cells to <paramref name="output"/>: every cell inside a live
    /// tip's cluster with hydration at least 1 that the graph does not hold and the space leaves
    /// free, each once, carrying the largest hydration any tip gives it, which is exactly
    /// <see cref="HydrationAt"/> by construction. Tips are visited in index order and offsets in
    /// a fixed x, y, z order, and cells are emitted in first-visit order through a list, so the
    /// output depends only on the graph and never on dictionary order. A leafless species adds nothing.
    /// </summary>
    public static void Collect(TreeGraph graph, TreeSpecies species, ITreeSpace space, List<TreeCell> output)
    {
        if (!species.HasLeaves)
            return;

        int radius = species.LeafRadius;
        var shape = species.LeafShape;
        // Cell -> position in the list; membership only, emission walks the list.
        var index = new Dictionary<long, int>();
        var cells = new List<(int X, int Y, int Z, int H)>();

        for (int i = 0; i < graph.Count; i++)
        {
            if (!IsLiveTip(graph, species, i))
                continue;
            int tx = graph[i].X, ty = graph[i].Y, tz = graph[i].Z;

            for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                if (!InShape(shape, radius, dx, dy, dz))
                    continue;
                int h = species.LeafHydration - Distance(dx, dy, dz);
                if (h < 1)
                    continue;
                int x = tx + dx, y = ty + dy, z = tz + dz;
                if (graph.Occupies(x, y, z) || !space.IsFree(x, y, z))
                    continue;

                long key = TreeCellKey.Pack(x, y, z);
                if (index.TryGetValue(key, out int at))
                {
                    if (h > cells[at].H)
                        cells[at] = (x, y, z, h);
                }
                else
                {
                    index[key] = cells.Count;
                    cells.Add((x, y, z, h));
                }
            }
        }

        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            output.Add(new TreeCell(c.X, c.Y, c.Z, species.LeavesId,
                LeafState.Encode(Math.Min(c.H, LeafState.MaxHydration), false)));
        }
    }
}
