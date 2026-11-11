namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// Where a battle tower's roof altar stands in its one fight (<see cref="TowerRecord.AltarState"/>): saved in
/// <c>stations.json</c> and sent as one byte per tower in the station frame.
/// </summary>
public enum TowerAltarState : byte
{
    /// <summary>Nothing has been started: the ritual may begin once every floor's door is open.</summary>
    Dormant,

    /// <summary>The ritual is under way; the Skeleton King rises when its timer runs out.</summary>
    Ritual,

    /// <summary>The Skeleton King walks the roof.</summary>
    Awake,

    /// <summary>The Skeleton King was killed: the altar never wakes again.</summary>
    Defeated,
}
