using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;
using VoxelEngine.Entities.TowerMobs;

namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// Raises a tower floor's skeletons from its bone piles. Every pile holds exactly one skeleton and lies at a free
/// position on the floor: it wakes when a non-immune target stands within <see cref="MobConfig.SkeletonPileTriggerBlocks"/>
/// on its floor (<see cref="TowerRules.PileTriggers"/>), raises its skeleton bound to the floor and is then gone. A pile
/// never raises past <see cref="TowerDifficulty.PileNearLimit"/> living skeletons within
/// <see cref="MobConfig.SkeletonPileNearRadiusBlocks"/> of it (5 on the ground floor, 1 more per floor), so it waits for
/// kills while its surroundings are full. A pile a chest has alarmed (<see cref="PileState.Alarmed"/>) raises at once, past
/// its trigger range and the near limit (the hard cap still holds). A pile in an unloaded chunk freezes. A skeleton the
/// floor is owed (despawned or strayed) lies as a pile where it stood (<see cref="TowerFloorState.OwedPiles"/>) and rises
/// from there the same way, so a floor's skeletons always show as piles until they rise. It writes no block. Main thread,
/// authoritative worlds only.
/// </summary>
public sealed class SkeletonPileManager
{
    /// <summary>
    /// Ticks the piles of <paramref name="floor"/> against <paramref name="targets"/>, raising the skeletons of the piles
    /// that wake through <paramref name="skeletons"/>. Returns how many skeletons rose, so the caller counts them as
    /// living in the same frame.
    /// </summary>
    public int Update(TowerFloorState floor, ChunkManager chunks, SkeletonManager skeletons,
        IReadOnlyList<IMobTarget> targets)
    {
        var total = 0;
        foreach (var pile in floor.Piles)
        {
            if (pile.Raised || !Wakes(floor, pile.Spot, pile.Alarmed, chunks, skeletons, targets)) continue;
            if (!Raise(floor, pile.Spot, chunks, skeletons)) continue;
            pile.Alarmed = false;
            pile.Raised = true;
            total++;
        }

        var owed = floor.OwedPiles;
        for (var i = owed.Count - 1; i >= 0; i--)
        {
            if (!Wakes(floor, owed[i], false, chunks, skeletons, targets) || !Raise(floor, owed[i], chunks, skeletons))
                continue;
            owed.RemoveAt(i);
            total++;
        }
        return total;
    }

    // Whether the pile at spot is in a loaded chunk and woken, by a chest alarm or by a non-immune target in range
    private static bool Wakes(TowerFloorState floor, Vector3 spot, bool alarmed, ChunkManager chunks,
        SkeletonManager skeletons, IReadOnlyList<IMobTarget> targets)
    {
        if (chunks.ChunkAt((int)MathF.Floor(spot.X), (int)MathF.Floor(spot.Y), (int)MathF.Floor(spot.Z)) == null) return false;
        if (alarmed) return true;
        for (var t = 0; t < targets.Count; t++)
        {
            var target = targets[t];
            if (target.IsImmune) continue;
            if (TowerRules.PileTriggers(spot, floor.BottomY, floor.TopY, target.Eye, MobConfig.SkeletonPileTriggerBlocks))
                return skeletons.LivingCountNear(spot, MobConfig.SkeletonPileNearRadiusBlocks)
                       < TowerDifficulty.PileNearLimit(floor.Ref.Floor);
        }
        return false;
    }

    // Raises one skeleton at the pile's spot, bound to the floor, or at the nearest open column when the spot is walled
    // in; false when the hard cap refuses it or no column is open
    private static bool Raise(TowerFloorState floor, Vector3 spot, ChunkManager chunks, SkeletonManager skeletons)
    {
        var x = (int)MathF.Floor(spot.X);
        var y = (int)MathF.Floor(spot.Y);
        var z = (int)MathF.Floor(spot.Z);
        bool IsOpen(int cx, int cy, int cz) => chunks.TryGetCell(cx, cy, cz, out var id, out _) && !BlockType.IsSolid(id);
        var at = new Vector3(spot.X, y + MobConfig.SkeletonHeight * 0.5f, spot.Z);
        if (!IsOpen(x, y, z) || !IsOpen(x, y + 1, z))
        {
            var points = TowerRules.SpawnPoints(x, y, z, 1, IsOpen);
            if (points.Count == 0) return false;
            at = points[0];
        }
        var skeleton = skeletons.Spawn(at);
        if (skeleton == null) return false;
        skeleton.Floor = floor.Ref;
        return true;
    }
}
