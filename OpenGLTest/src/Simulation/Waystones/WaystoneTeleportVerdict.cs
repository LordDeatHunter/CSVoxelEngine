namespace VoxelEngine.Simulation.Waystones;

/// <summary>What a waystone teleport request came to (<see cref="WaystoneTeleport.Judge"/>): allowed, or the first
/// rule it broke.</summary>
public enum WaystoneTeleportVerdict
{
    /// <summary>Allowed: the player travels (and pays, unless free).</summary>
    Teleport,

    /// <summary>The player does not stand at a waystone it may use (not a loaded waystone, or out of reach).</summary>
    NotAtWaystone,

    /// <summary>The destination is the waystone the player stands at.</summary>
    Current,

    /// <summary>The player never discovered the destination.</summary>
    NotDiscovered,

    /// <summary>The world's registry holds no row for the destination.</summary>
    Unknown,

    /// <summary>The destination's block was broken.</summary>
    Lost,

    /// <summary>The player's mana is not more than half full.</summary>
    NeedsMana,
}
