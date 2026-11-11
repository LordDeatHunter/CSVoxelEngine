namespace VoxelEngine.Simulation.Waystones;

/// <summary>The realm a waystone stands in (<see cref="WaystoneRealms.Classify"/>), shown as a tag in the teleport list.</summary>
public enum WaystoneRealm
{
    /// <summary>On the overworld's ground (beside a surface ruin).</summary>
    Surface,

    /// <summary>Below the overworld's ground, above the barrier (a sealed crypt).</summary>
    Underground,

    /// <summary>Below the barrier band, in hell's layers.</summary>
    Hell,

    /// <summary>In the sky island band.</summary>
    Sky,
}
