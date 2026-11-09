namespace VoxelEngine.Entities.TowerMobs;

/// <summary>
/// How a tower skeleton fights, fixed at its spawn and named by its entity kind (<see cref="Skeleton.EntityName"/>,
/// <see cref="Skeleton.WarriorEntityName"/>): the archer keeps its distance and shoots, punching only what comes too
/// close; the warrior closes in and stabs with its copper dagger. The value rides the skeleton's wire byte
/// (<see cref="Skeleton.PackLook"/>).
/// </summary>
public enum SkeletonRole : byte
{
    Archer = 0,
    Warrior = 1,
}
