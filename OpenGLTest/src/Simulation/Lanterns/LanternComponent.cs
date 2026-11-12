namespace VoxelEngine.Simulation.Lanterns;

/// <summary>Bounded per-instance lantern metadata, copied when the item changes owner.</summary>
public readonly record struct LanternComponent(LanternState State);
