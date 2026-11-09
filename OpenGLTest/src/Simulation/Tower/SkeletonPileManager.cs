using System;
using System.Collections.Generic;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.WorldGen.Structures;

namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// Raises a tower floor's skeletons from its piles. A pile whose cell is loaded and still holds the pile block wakes
/// when a non-immune target stands within <see cref="MobConfig.SkeletonPileTriggerBlocks"/> on its floor
/// (<see cref="TowerRules.PileTriggers"/>), and then raises a wave of up to <see cref="TowerDifficulty.PackSize"/>
/// skeletons of the floor's tier every <see cref="MobConfig.SkeletonPileWaveSeconds"/>, each bound to the floor, until
/// its waves run out. A wave counts only when at least one skeleton rose, so a full hard cap or a walled-in pile keeps
/// it. A pile in an unloaded chunk freezes; a loaded pile cell that lost its block counts as depleted. Skeletons the
/// floor is owed (despawned or strayed) rise again at any of its piles, depleted or not, on the same cooldown. It
/// writes no block (the tower's sweep clears depleted piles). Main thread, authoritative worlds only.
/// </summary>
public sealed class SkeletonPileManager
{
    /// <summary>
    /// Ticks the piles of <paramref name="floor"/> against <paramref name="targets"/>, raising its waves and then the
    /// skeletons it is owed through <paramref name="skeletons"/>. Returns how many skeletons rose, so the caller counts
    /// them as living in the same frame.
    /// </summary>
    public int Update(TowerFloorState floor, float dt, ChunkManager chunks, SkeletonManager skeletons,
        IReadOnlyList<IMobTarget> targets)
    {
        var pack = TowerDifficulty.PackSize(floor.Ref.Floor);
        var piles = floor.Piles;
        var total = 0;
        for (var i = 0; i < piles.Length; i++)
        {
            var pile = piles[i];
            var (x, y, z) = pile.Cell;
            if (!chunks.TryGetCell(x, y, z, out var id, out _)) continue;
            pile.Cooldown = Math.Max(0f, pile.Cooldown - dt);
            if (pile.Depleted) continue;
            if (id != TowerPalette.PileBlock)
            {
                pile.WavesLeft = 0;
                continue;
            }
            if (pile.Cooldown > 0f || !Triggered(floor, pile, targets)) continue;
            var raised = Raise(floor, pile, pack, chunks, skeletons);
            if (raised == 0) continue;
            total += raised;
            pile.WavesLeft--;
            pile.Cooldown = MobConfig.SkeletonPileWaveSeconds;
        }

        while (floor.Owed > 0)
        {
            var pile = OwedPile(floor, chunks, targets);
            if (pile == null) return total;
            var risen = Raise(floor, pile, Math.Min(floor.Owed, pack), chunks, skeletons);
            pile.Cooldown = MobConfig.SkeletonPileWaveSeconds;
            if (risen == 0) return total;
            floor.Owed -= risen;
            total += risen;
        }
        return total;
    }

    // The first loaded pile, depleted or not, that is off its cooldown and woken by a target
    private static PileState? OwedPile(TowerFloorState floor, ChunkManager chunks, IReadOnlyList<IMobTarget> targets)
    {
        var piles = floor.Piles;
        for (var i = 0; i < piles.Length; i++)
        {
            var pile = piles[i];
            var (x, y, z) = pile.Cell;
            if (pile.Cooldown > 0f || chunks.ChunkAt(x, y, z) == null || !Triggered(floor, pile, targets)) continue;
            return pile;
        }
        return null;
    }

    private static bool Triggered(TowerFloorState floor, PileState pile, IReadOnlyList<IMobTarget> targets)
    {
        for (var t = 0; t < targets.Count; t++)
        {
            var target = targets[t];
            if (target.IsImmune) continue;
            if (TowerRules.PileTriggers(pile.Cell, floor.BottomY, floor.TopY, target.Eye,
                    MobConfig.SkeletonPileTriggerBlocks))
                return true;
        }
        return false;
    }

    // Raises up to count skeletons around the pile, bound to its floor; stops at the first refused spawn (hard cap)
    private static int Raise(TowerFloorState floor, PileState pile, int count, ChunkManager chunks,
        SkeletonManager skeletons)
    {
        var (x, y, z) = pile.Cell;
        var points = TowerRules.SpawnPoints(x, y, z, count,
            (cx, cy, cz) => chunks.TryGetCell(cx, cy, cz, out var id, out _) && !BlockType.IsSolid(id));
        var risen = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var skeleton = skeletons.Spawn(points[i], floor.Ref.Floor);
            if (skeleton == null) break;
            skeleton.Floor = floor.Ref;
            risen++;
        }
        return risen;
    }
}
