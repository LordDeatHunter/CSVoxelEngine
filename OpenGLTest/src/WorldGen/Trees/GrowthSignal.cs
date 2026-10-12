using VoxelEngine.WorldGen.Trees.Logics;

namespace VoxelEngine.WorldGen.Trees;

/// <summary>
/// One step of a growth pulse as an <see cref="IGrowthLogic"/> sees it: the node the signal
/// stands on (its cell copied out of the graph), the direction it arrived from, the energy left
/// and how often it has turned. <see cref="TreeGrower"/> builds it fresh each step before any
/// <see cref="TreeGraph.AddChild"/>, so it never holds a stale node reference. Pass it by
/// <c>in</c>.
/// </summary>
public readonly struct GrowthSignal(
    TreeGraph graph,
    TreeSpecies species,
    ITreeSpace space,
    int node,
    int nodeX,
    int nodeY,
    int nodeZ,
    int incomingDir,
    int energy,
    int turns)
{
    /// <summary>The tree being grown.</summary>
    public readonly TreeGraph Graph = graph;

    /// <summary>The tree's species.</summary>
    public readonly TreeSpecies Species = species;

    /// <summary>The obstacle view growth is checked against.</summary>
    public readonly ITreeSpace Space = space;

    /// <summary>Index of the node the signal stands on.</summary>
    public readonly int Node = node;

    /// <summary>Cell of that node, copied when the signal was built.</summary>
    public readonly int NodeX = nodeX, NodeY = nodeY, NodeZ = nodeZ;

    /// <summary>Direction the signal traveled to reach the node (<see cref="TreeDirections.Up"/> at the root).</summary>
    public readonly int IncomingDir = incomingDir;

    /// <summary>Energy left before this step.</summary>
    public readonly int Energy = energy;

    /// <summary>Number of times the signal changed direction so far.</summary>
    public readonly int Turns = turns;

    /// <summary>Height of the node above the root.</summary>
    public int Height => NodeY - Graph.RootY;

    /// <summary>Whether the signal has never left the vertical stem.</summary>
    public bool IsInTrunk => Turns == 0;
}
