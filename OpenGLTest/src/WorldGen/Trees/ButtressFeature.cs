namespace silkgltriangle;

/// <summary>
/// Grows a wide trunk's buttress fins: straight runs of full-cube wood along the ground at the
/// root's Y, one per cardinal side that wins its <see cref="WorldGenerationConfig.TreeButtressFinChance"/>
/// draw, reaching from the root out past the trunk's <see cref="TreeRasterizer.BlockRadius"/>
/// footprint by 1..<see cref="WorldGenerationConfig.TreeButtressMaxReach"/> blocks. A fin stops at
/// the first cell the graph already holds, that the space does not leave free, or that would hang
/// over a free cell, so every fin cell rests on the ground. Fins are feature nodes (never leaf
/// tips) and are full cubes, so the on-axis fin cells stand in for the on-axis shells and the
/// off-axis shells chain through them (the rasteriser's full-cube feature-parent rule). A root
/// under one block of shell radius grows none. Stateless: the random draws are made for every
/// side whatever the obstacles, so the stream never depends on the surroundings.
/// </summary>
internal sealed class ButtressFeature : ITreeFeature
{
    /// <inheritdoc />
    public string Name => "buttress";

    /// <inheritdoc />
    public void Apply(TreeGraph graph, TreeSpecies species, ITreeSpace space, int depth, SplitMix64 rng)
    {
        // Copy before any AddChild: the node reference is invalidated by a reallocation.
        float rootPx = graph[TreeGraph.Root].ThicknessPx;
        float rootPipe = graph[TreeGraph.Root].PipePx;
        int x0 = graph.RootX, y = graph.RootY, z0 = graph.RootZ;
        int r = TreeRasterizer.BlockRadius(rootPx);
        if (r == 0)
            return;
        float finPx = System.Math.Min(WoodState.PixelsPerBlock, species.MaxPx);
        // A fin is a full cube drawn over the girth-swollen root, but its pipe never exceeds the
        // root's, so the parent-at-least-child rule holds along the fin
        float finPipe = System.Math.Min(rootPipe, finPx);

        for (int dir = 0; dir < TreeDirections.Count; dir++)
        {
            if (!TreeDirections.IsSide(dir))
                continue;
            bool grows = rng.NextFloat() < WorldGenerationConfig.TreeButtressFinChance;
            int len = rng.NextInt(1, WorldGenerationConfig.TreeButtressMaxReach + 1);
            if (!grows)
                continue;

            int dx = TreeDirections.Dx[dir], dz = TreeDirections.Dz[dir];
            int parent = TreeGraph.Root;
            for (int k = 1; k <= r + len; k++)
            {
                int x = x0 + dx * k, z = z0 + dz * k;
                if (graph.Occupies(x, y, z) || !space.IsFree(x, y, z) || space.IsFree(x, y - 1, z))
                    break;
                parent = graph.AddChild(parent, dir, finPx, feature: true);
                graph.SetThickness(parent, finPipe, finPx);
            }
        }
    }
}
