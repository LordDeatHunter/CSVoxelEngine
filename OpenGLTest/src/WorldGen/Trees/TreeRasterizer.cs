using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// Turns a grown <see cref="TreeGraph"/> into the cells it writes: wood with its
/// <see cref="WoodState"/> (thickness code from the displayed diameter, parent direction from
/// the graph, <see cref="WoodState.ParentRoot"/> on the root, never a shell this phase) and
/// leaves from <see cref="LeafCluster"/> with their <see cref="LeafState"/> hydration. Pure and
/// stateless.
/// </summary>
public static class TreeRasterizer
{
    /// <summary>
    /// Thickness code of a diameter: <c>ceil(px) - 1</c>, clamped to 0..<see cref="WoodState.FullCube"/>
    /// (no shells yet, so anything over 16 px is a full cube). Monotone in <paramref name="px"/>.
    /// </summary>
    public static int ThicknessCode(float px) => Math.Clamp((int)MathF.Ceiling(px) - 1, 0, WoodState.FullCube);

    /// <summary>The wood state byte of a node.</summary>
    public static byte WoodStateOf(in TreeGraph.Node n) =>
        WoodState.Encode(ThicknessCode(n.ThicknessPx), n.Parent < 0 ? WoodState.ParentRoot : n.ParentDir, false);

    /// <summary>
    /// Rasterises the tree in a fixed order: every node in index order as wood (growth nodes
    /// first, then feature nodes such as roots, since features append after growth), then the leaves.
    /// </summary>
    public static List<TreeCell> Rasterize(TreeGraph graph, TreeSpecies species, ITreeSpace space)
    {
        var output = new List<TreeCell>(graph.Count * 8);
        for (int i = 0; i < graph.Count; i++)
        {
            ref readonly var n = ref graph[i];
            output.Add(new TreeCell(n.X, n.Y, n.Z, species.WoodId, WoodStateOf(in n)));
        }
        LeafCluster.Collect(graph, species, space, output);
        return output;
    }

    /// <summary>
    /// The box every rasterised cell of the species stays inside, relative to the root:
    /// horizontally and upward the signal energy (no growth node is further than that from the
    /// root) plus the leaf radius; downward the larger of the leaf radius and the roots
    /// feature's depth. Relies on the grower never descending (<c>TreeGrower.MaskBlocked</c>);
    /// phase 3 reads it for the chunk-border margin.
    /// </summary>
    public static (int Horizontal, int Up, int Down) ReachOf(TreeSpecies species)
    {
        int leaf = species.HasLeaves ? species.LeafRadius : 0;
        int roots = 0;
        for (int i = 0; i < species.Features.Count; i++)
        {
            if (species.Features[i].Feature.Name == "roots")
                roots = Math.Max(roots, species.Features[i].Depth);
        }
        int reach = species.SignalEnergy + leaf;
        return (reach, reach, Math.Max(leaf, roots));
    }
}
