namespace VoxelEngine.Config;

/// <summary>
/// Mana tuning (<see cref="VoxelEngine.Player.PlayerMana"/>): a small pool that grows with the spell parts
/// the player knows, and a slow refill. A typical spell should cost 20-40% of the pool, so the player has
/// to choose what to cast (the spell costs themselves come with the part data, MG-2).
/// </summary>
public static class ManaConfig
{
    /// <summary>The pool of a player who knows no spell parts.</summary>
    public const int StartMax = 100;

    /// <summary>How much the pool grows for each spell part the player knows.</summary>
    public const int MaxPerKnownPart = 10;

    /// <summary>The largest pool; the wire reader clamps a reported max to it.</summary>
    public const int MaxCap = 300;

    /// <summary>
    /// Seconds an empty pool takes to refill. The rate scales with the max, so a grown pool refills in
    /// the same time.
    /// </summary>
    public const float RegenFullSeconds = 150f;
}
