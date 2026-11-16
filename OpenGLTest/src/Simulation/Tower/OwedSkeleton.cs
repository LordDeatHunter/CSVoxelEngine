using System.Numerics;
using VoxelEngine.Entities.TowerMobs;

namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// A bound skeleton that left its floor without being killed (despawned, leashed, crumbled, or lost to a restore
/// timeout): the floor it is owed to and the spot on the floor where it stood, which becomes a bone pile there.
/// </summary>
public readonly record struct OwedSkeleton(TowerFloorRef Floor, Vector3 Spot)
{
    /// <summary>The owed skeleton whose body center was <paramref name="bodyCenter"/>: its pile lies at its feet.</summary>
    public static OwedSkeleton At(TowerFloorRef floor, Vector3 bodyCenter) =>
        new(floor, bodyCenter - new Vector3(0f, MobConfig.SkeletonHeight * 0.5f, 0f));
}
