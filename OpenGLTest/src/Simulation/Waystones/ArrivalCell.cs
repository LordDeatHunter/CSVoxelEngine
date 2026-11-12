namespace VoxelEngine.Simulation.Waystones;

/// <summary>What one cell is to the waystone arrival search (<see cref="IArrivalCells"/>).</summary>
public enum ArrivalCell : byte
{
    /// <summary>The cell's chunk is not loaded: the search cannot decide yet.</summary>
    Unloaded,

    /// <summary>A body may stand in it: not solid and not a fluid (air, a plant, a lantern).</summary>
    Open,

    /// <summary>A solid block a body stands on (and cannot stand in).</summary>
    Solid,

    /// <summary>Neither: a fluid, which a body neither stands on nor arrives in.</summary>
    Blocked,
}
