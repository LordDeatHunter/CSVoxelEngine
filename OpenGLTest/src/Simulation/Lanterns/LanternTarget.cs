namespace VoxelEngine.Simulation.Lanterns;

/// <summary>Positive instance names the acting player's selected lantern; zero names a placed cell.</summary>
public readonly record struct LanternTarget(int Instance, (int X, int Y, int Z) Cell);
