namespace VoxelEngine.Simulation.Waystones;

/// <summary>
/// What one player's activation of a waystone came to (<see cref="WaystoneManager.DiscoverFor"/>): the registry row,
/// whether this player may rename it (its first discoverer) and whether this activation added it to the player's
/// discovered list (the discovery popup shows only then).
/// </summary>
public readonly record struct WaystoneDiscovery(WaystoneRecord Record, bool FirstDiscoverer, bool NewToPlayer);
