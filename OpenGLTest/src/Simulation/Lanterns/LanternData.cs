using VoxelEngine.Saves;

namespace VoxelEngine.Simulation.Lanterns;

/// <summary>Versioned, name-based lantern state shared by inventory, placed, drop saves and wire snapshots.</summary>
public sealed class LanternData
{
    public int Version { get; set; } = 1;
    public int[]? Cell { get; set; }
    public string? Block { get; set; }
    public float Remaining { get; set; }
    public float Capacity { get; set; }
    public StationSaveData.ChestData? Storage { get; set; }
}
