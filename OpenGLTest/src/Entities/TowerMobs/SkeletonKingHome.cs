using System.Numerics;

namespace VoxelEngine.Entities.TowerMobs;

/// <summary>
/// Where a Skeleton King belongs: the battle tower it rose from (its key, null for a scenario's, a debugger's or a
/// client mirror's unbound king), the feet it stands on at home (on top of the roof altar), how far it may walk
/// from there before it turns back (<see cref="SkeletonKing.HomeMove"/>).
/// </summary>
/// <param name="Tower">The tower's key (its cell), or null for an unbound king.</param>
/// <param name="Feet">The feet position the king rises at and is put back on.</param>
/// <param name="LeashRadius">How far, horizontally, from <paramref name="Feet"/> the king may go.</param>
public readonly record struct SkeletonKingHome((int CellX, int CellZ)? Tower, Vector3 Feet, float LeashRadius)
{
    /// <summary>A home bound to no tower: feet at <paramref name="feet"/>, leashed only by
    /// <see cref="MobConfig.SkeletonKingLeaveRadiusBlocks"/>.</summary>
    public static SkeletonKingHome Unbound(Vector3 feet) =>
        new(null, feet, MobConfig.SkeletonKingLeaveRadiusBlocks);
}
