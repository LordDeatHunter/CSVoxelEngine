namespace VoxelEngine.Simulation.Tower;

/// <summary>What a right-click on a battle tower's altar came to (<see cref="TowerRules.AltarVerdictOf"/>).</summary>
public enum AltarVerdict
{
    /// <summary>Every floor is cleared and nothing was started: the ritual begins.</summary>
    Begin,

    /// <summary>A floor's door is still shut: the seals hold and nothing happens.</summary>
    Sealed,

    /// <summary>The ritual is already under way.</summary>
    Stirring,

    /// <summary>The Skeleton King already walks.</summary>
    Awake,

    /// <summary>The Skeleton King of this tower was killed; the altar stays silent for good.</summary>
    Defeated,

    /// <summary>The cell is no tracked tower's altar.</summary>
    NoTower,
}
