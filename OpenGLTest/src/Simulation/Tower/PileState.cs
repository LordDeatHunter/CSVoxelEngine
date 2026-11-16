namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// One bone pile of a tower floor, an entity-style spot rather than a block: it lies at a free position on the floor,
/// holds exactly one skeleton, and is visible until that skeleton rises. Main thread only.
/// </summary>
public sealed class PileState(System.Numerics.Vector3 spot, bool raised = false)
{
    /// <summary>Where the pile lies: its center on the floor surface, not aligned to the block grid.</summary>
    public System.Numerics.Vector3 Spot { get; } = spot;

    /// <summary>Whether its skeleton has risen (saved). A raised pile is gone for good: the skeleton, if it later
    /// despawns, leaves a fresh pile where it stood (<see cref="TowerFloorState.OwedPiles"/>).</summary>
    public bool Raised { get; set; } = raised;

    /// <summary>Whether a chest opened nearby has forced it awake: its skeleton rises at once, ignoring its trigger
    /// range and the near-pile skeleton limit. Cleared once it rises; never saved.</summary>
    public bool Alarmed { get; set; }

    /// <summary>Whether it has raised its skeleton.</summary>
    public bool Depleted => Raised;
}
