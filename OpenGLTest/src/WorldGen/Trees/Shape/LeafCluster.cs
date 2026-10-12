using System;
using System.Collections.Generic;

namespace VoxelEngine.WorldGen.Trees.Shape;

/// <summary>
/// The leaf rule, a pure function of a grown <see cref="TreeGraph"/> plus the obstacles of an
/// <see cref="ITreeSpace"/>: every <b>live tip</b> (a childless, non-feature node at or under the
/// species' <see cref="TreeSpecies.TipMaxPx"/>) carries a cluster of the species'
/// <see cref="TreeSpecies.LeafShape"/>, at the tree's <see cref="ClusterRadius(TreeSpecies, int)"/>:
/// the radius grows from <see cref="TreeSpecies.LeafMinRadius"/> to
/// <see cref="TreeSpecies.LeafRadius"/> with the tree's <see cref="SupportPx"/> (its root
/// diameter), so a thin young trunk carries a small crown and every tip of one tree shares one
/// radius. The convention, stated once: a leaf's stored hydration is
/// <see cref="TreeSpecies.LeafHydration"/> minus the Manhattan <see cref="Distance"/> to the
/// nearest live tip whose cluster shape contains the cell; a leaf is emitted only while that is at
/// least 1, and 0 would decay. The radius changes shape membership only, never that number.
/// Phase 5's <c>LeafDecayManager</c> re-derives the same number on world cells through
/// <see cref="HydrationFromTip(TreeSpecies, int, int, int, int)"/>, the one per-tip formula this
/// rule is built from.
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
    /// The tree's support in pixels: the root node's diameter quantised exactly as
    /// <see cref="TreeRasterizer"/> writes it, so it equals the <see cref="WoodState.DiameterPx"/>
    /// stored in the root cell (capped at one block, the most a wood state byte holds). That is
    /// what lets the world side re-derive the same number from the root cell alone.
    /// </summary>
    public static int SupportPx(TreeGraph graph) => TreeRasterizer.ThicknessCode(graph[TreeGraph.Root].ThicknessPx) + 1;

    /// <summary>
    /// The cluster radius every live tip of a tree with <paramref name="supportPx"/> of support
    /// wears: <see cref="TreeSpecies.LeafMinRadius"/> plus the share of the span up to
    /// <see cref="TreeSpecies.LeafRadius"/> that the support covers of
    /// <see cref="TreeSpecies.LeafFullRadiusPx"/>, in integer math, so it is non-decreasing in
    /// the support and reaches the full radius at <c>LeafFullRadiusPx</c>. 0 for a leafless species.
    /// </summary>
    public static int ClusterRadius(TreeSpecies species, int supportPx)
    {
        if (!species.HasLeaves)
            return 0;
        int span = species.LeafRadius - species.LeafMinRadius;
        int covered = Math.Clamp(supportPx, 0, species.LeafFullRadiusPx);
        return species.LeafMinRadius + span * covered / species.LeafFullRadiusPx;
    }

    /// <summary><see cref="ClusterRadius(TreeSpecies, int)"/> at the graph's <see cref="SupportPx"/>.</summary>
    public static int ClusterRadius(TreeSpecies species, TreeGraph graph)
        => species.HasLeaves ? ClusterRadius(species, SupportPx(graph)) : 0;

    /// <summary>
    /// Whether the offset from a tip lies inside its cluster. A blob is the ball of
    /// <paramref name="radius"/>; a cone hangs from the tip's level down over
    /// <paramref name="radius"/> levels, level <c>k</c> below the tip having horizontal radius
    /// <c>k + 1</c>, so it widens downward. A pad is a flat cap resting on the tip: the disc of
    /// horizontal radius <c>max(1, radius - 1)</c> at the tip's own level, the disc of
    /// <paramref name="radius"/> one level above, and nothing below the tip, so the limb under it
    /// stays bare. A fan adds the full disc of <paramref name="radius"/> one level below the tip,
    /// so the crown has some depth. Every shape is nested in its radius: an offset inside
    /// the shape at radius <c>r'</c> is inside it at every <c>r &gt;= r'</c>, which is what keeps
    /// the decay manager's full-radius, best-first offset table exact at a smaller radius.
    /// </summary>
    public static bool InShape(LeafClusterShape shape, int radius, int dx, int dy, int dz)
    {
        if (shape is LeafClusterShape.Pad or LeafClusterShape.Fan)
        {
            int horizontal = dx * dx + dz * dz;
            if (dy == 0)
            {
                int m = Math.Max(1, radius - 1);
                return horizontal <= m * m;
            }
            if (dy == 1) return horizontal <= radius * radius;
            return shape == LeafClusterShape.Fan && dy == -1 && horizontal <= radius * radius;
        }
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
    /// The hydration one live tip gives the cell at offset (<paramref name="dx"/>,
    /// <paramref name="dy"/>, <paramref name="dz"/>) from it in a cluster of
    /// <paramref name="radius"/>: <c>LeafHydration - Distance</c> while the species' cluster shape
    /// at that radius holds the offset, floored at 0, else 0. The one per-tip formula the graph
    /// rule here and the decay manager's world rule share; the radius only decides membership.
    /// </summary>
    public static int HydrationFromTip(TreeSpecies species, int radius, int dx, int dy, int dz)
        => InShape(species.LeafShape, radius, dx, dy, dz)
            ? Math.Max(0, species.LeafHydration - Distance(dx, dy, dz))
            : 0;

    /// <summary>
    /// <see cref="HydrationFromTip(TreeSpecies, int, int, int, int)"/> at the species' full
    /// <see cref="TreeSpecies.LeafRadius"/>: the largest cluster any tree of the species wears,
    /// which is what <c>LeafDecayManager.TipOffsets</c> precomputes. Because shapes are nested, a
    /// smaller tree's cluster is a subset of it with the same hydration on every shared offset.
    /// </summary>
    public static int HydrationFromTip(TreeSpecies species, int dx, int dy, int dz)
        => HydrationFromTip(species, species.LeafRadius, dx, dy, dz);

    /// <summary>
    /// The hydration the rule gives the cell: the maximum over live tips whose shape holds it of
    /// <c>LeafHydration - Distance</c>, or 0 when no tip's shape holds it (callers test
    /// <c>&gt;= 1</c>). Ignores obstacles: it is the value a leaf standing there carries. No allocation.
    /// </summary>
    public static int HydrationAt(TreeGraph graph, TreeSpecies species, int x, int y, int z)
    {
        if (!species.HasLeaves)
            return 0;

        int radius = ClusterRadius(species, graph);
        int best = 0;
        for (int i = 0; i < graph.Count; i++)
        {
            if (!IsLiveTip(graph, species, i))
                continue;
            ref readonly var t = ref graph[i];
            best = Math.Max(best, HydrationFromTip(species, radius, x - t.X, y - t.Y, z - t.Z));
        }
        return best;
    }

    /// <summary>
    /// Appends the tree's leaf cells to <paramref name="output"/>: every cell inside a live
    /// tip's cluster with hydration at least 1 that the graph does not hold and the space leaves
    /// free, each once, carrying the largest hydration any tip gives it, which is exactly
    /// <see cref="HydrationAt"/> by construction. Tips are visited in index order and offsets in
    /// a fixed x, y, z order, and cells are emitted in first-visit order through a list, so the
    /// output depends only on the graph and never on dictionary order. A leafless species adds nothing.
    /// Cells in <paramref name="exclude"/> (the rasteriser's shells, packed by
    /// <see cref="TreeCellKey.Pack"/>) are skipped like the graph's own cells.
    /// </summary>
    public static void Collect(TreeGraph graph, TreeSpecies species, ITreeSpace space, List<TreeCell> output,
        HashSet<long>? exclude = null)
    {
        if (!species.HasLeaves)
            return;

        // One radius for the whole tree; a smaller crown is also a smaller loop
        int radius = ClusterRadius(species, graph);
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
                int h = HydrationFromTip(species, radius, dx, dy, dz);
                if (h < 1)
                    continue;
                int x = tx + dx, y = ty + dy, z = tz + dz;
                if (graph.Occupies(x, y, z) || !space.IsFree(x, y, z))
                    continue;

                long key = TreeCellKey.Pack(x, y, z);
                if (exclude != null && exclude.Contains(key))
                    continue;
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
