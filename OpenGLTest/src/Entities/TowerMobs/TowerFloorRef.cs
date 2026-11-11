namespace VoxelEngine.Entities.TowerMobs;

/// <summary>
/// The battle tower (by its placement cell, <paramref name="CellX"/>, <paramref name="CellZ"/>) and the floor
/// (0 at the ground) whose lock a <see cref="Skeleton"/> counts toward: the floor's door stays shut while a
/// skeleton tagged with it lives, or is owed back to it after despawning.
/// </summary>
public readonly record struct TowerFloorRef(int CellX, int CellZ, int Floor);
