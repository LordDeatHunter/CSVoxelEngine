using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Simulation;

/// <summary>
/// The outcome of one tree growth call: its status, the root cell it grew from (the target
/// column and height hint when nothing was found), the graph nodes it added and the cells it
/// wrote, split into wood writes (new cells and thickness rewrites), new leaves and hydration
/// raises on existing leaves.
/// </summary>
public readonly record struct TreeGrowthResult(
    TreeGrowthStatus Status,
    int RootX,
    int RootY,
    int RootZ,
    int NodesAdded,
    int WoodWritten,
    int LeavesPlaced,
    int HydrationRaised)
{
    /// <summary>Whether a tree was grown (an existing one or a planted sapling).</summary>
    public bool Succeeded => Status is TreeGrowthStatus.Grew or TreeGrowthStatus.Planted;

    /// <summary>Every write that changes a mesh; hydration-only rewrites do not (the mesher never
    /// reads <see cref="LeafState"/>).</summary>
    public int MeshCellsWritten => WoodWritten + LeavesPlaced;

    /// <summary>Every cell the call wrote.</summary>
    public int CellsWritten => MeshCellsWritten + HydrationRaised;
}
