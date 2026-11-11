namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// What became of the Skeleton King bound to one tower (<see cref="SkeletonKingManager.StatusFor"/>), which the
/// tower's altar settles its state from. A kill and a despawn are told apart by health: a death lowers it to 0,
/// a despawn (<see cref="Entities.Mob.BeginDying"/>) never touches it.
/// </summary>
public enum KingStatus
{
    /// <summary>No king is bound to the tower.</summary>
    None,

    /// <summary>A king bound to the tower is alive: not dying, with health left.</summary>
    Alive,

    /// <summary>The tower's king was killed: its health is 0, whether it is already playing its death or not yet.</summary>
    Killed,

    /// <summary>The tower's king is fading out with health left: it despawned (everyone left), never killed.</summary>
    Fading,
}
