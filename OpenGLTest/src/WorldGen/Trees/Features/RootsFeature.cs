namespace VoxelEngine.WorldGen.Trees.Features;

/// <summary>
/// Grows the base cell's root straight down through free cells, up to the feature depth, the
/// way the old <c>TreeGenerator</c> root loop reached ground under post-carve air: it stops at
/// the first cell the space or the graph does not leave free. Root cells are feature nodes with
/// the base's thickness, so they are never leaf tips. Stateless; the random stream is unused.
/// </summary>
internal sealed class RootsFeature : ITreeFeature
{
    /// <inheritdoc />
    public string Name => "roots";

    /// <inheritdoc />
    public void Apply(TreeGraph graph, TreeSpecies species, ITreeSpace space, int depth, SplitMix64 rng)
    {
        // Copy before any AddChild: the node reference is invalidated by a reallocation.
        float pipe = graph[TreeGraph.Root].PipePx;
        float thick = graph[TreeGraph.Root].ThicknessPx;
        int x = graph.RootX, z = graph.RootZ;

        int parent = TreeGraph.Root;
        for (int d = 1; d <= depth; d++)
        {
            int y = graph.RootY - d;
            if (graph.Occupies(x, y, z) || !space.IsFree(x, y, z))
                break;
            parent = graph.AddChild(parent, TreeDirections.Down, thick, feature: true);
            graph.SetThickness(parent, pipe, thick);
        }
    }
}
