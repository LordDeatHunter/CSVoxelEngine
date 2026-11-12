namespace VoxelEngine.Simulation.Waystones;

/// <summary>
/// One waystone's row in the world's registry (<see cref="WaystoneManager"/>): its name, who discovered it first (the
/// only one who may rename it) and whether its block has been broken. Changed only through the manager.
/// </summary>
public sealed class WaystoneRecord(string name, string firstDiscoverer, bool lost = false)
{
    /// <summary>The name every player sees.</summary>
    public string Name { get; internal set; } = name;

    /// <summary>The discoverer id (<see cref="WaystoneManager.DiscovererOf"/>) of the first player to activate it.</summary>
    public string FirstDiscoverer { get; } = firstDiscoverer;

    /// <summary>True once the block is gone: it stays in its discoverers' lists, grayed out, and cannot be teleported to.</summary>
    public bool Lost { get; internal set; } = lost;
}
