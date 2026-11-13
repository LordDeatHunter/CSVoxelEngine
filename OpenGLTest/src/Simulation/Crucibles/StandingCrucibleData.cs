using VoxelEngine.Saves;

namespace VoxelEngine.Simulation.Crucibles;

/// <summary>One crucible standing on the ground in its save and wire shape: its cell, the crucible item's content
/// name and what it holds (by material name, never id).</summary>
public sealed class StandingCrucibleData
{
    public int[]? Cell { get; set; }
    public string? Item { get; set; }
    public PlayerSaveData.CrucibleData? Contents { get; set; }
}
