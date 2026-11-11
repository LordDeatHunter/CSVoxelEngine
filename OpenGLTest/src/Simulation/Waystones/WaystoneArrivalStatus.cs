namespace VoxelEngine.Simulation.Waystones;

/// <summary>How a waystone arrival search ended (<see cref="WaystoneArrivalSpot.Status"/>).</summary>
public enum WaystoneArrivalStatus
{
    /// <summary>A free standing spot beside the waystone, the traveler facing away from it.</summary>
    Found,

    /// <summary>Every cell was loaded but none was free: the traveler stands on top of the waystone.</summary>
    OnTop,

    /// <summary>Some cell the search needs is not loaded yet: hold the traveler and search again later.</summary>
    Unloaded,
}
