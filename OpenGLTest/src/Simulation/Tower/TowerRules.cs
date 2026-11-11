using System;
using System.Collections.Generic;
using System.Numerics;

namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// The battle tower's lock rules, GL-free and state-free: when a skeleton pile wakes for a target, when a floor is awake
/// at all, when a floor's door may open, where a pile raises its skeletons, when a bound skeleton has strayed too far
/// from its tower and what a click on the roof altar comes to. <see cref="SkeletonPileManager"/> and <c>TowerManager</c> apply them.
/// </summary>
public static class TowerRules
{
    // The columns a pile raises its skeletons in, in order: its own, the four sides, then the diagonals
    private static readonly (int Dx, int Dz)[] SpawnColumns =
        [(0, 0), (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];

    /// <summary>
    /// Whether a target whose eye is at <paramref name="eye"/> wakes the pile at <paramref name="pile"/>: its eye lies
    /// within <paramref name="radius"/> of the pile cell's center horizontally and inside the pile's floor band
    /// (above <paramref name="bottomY"/>, at most <paramref name="topY"/>), so a player on another floor never raises it.
    /// </summary>
    public static bool PileTriggers((int X, int Y, int Z) pile, int bottomY, int topY, Vector3 eye, float radius)
    {
        if (eye.Y <= bottomY || eye.Y > topY) return false;
        var dx = eye.X - (pile.X + 0.5f);
        var dz = eye.Z - (pile.Z + 0.5f);
        return dx * dx + dz * dz <= radius * radius;
    }

    /// <summary>
    /// Whether a target whose eye is at <paramref name="eye"/> stands on the floor between <paramref name="bottomY"/>
    /// (the slab it stands on) and <paramref name="topY"/> of the tower centered at (<paramref name="centerX"/>,
    /// <paramref name="centerZ"/>): inside the footprint's <paramref name="outerRadius"/> horizontally and inside the
    /// floor's band, so a player on another floor or outside the tower is not on it.
    /// </summary>
    public static bool OnFloor(Vector3 eye, int centerX, int centerZ, int outerRadius, int bottomY, int topY)
    {
        if (eye.Y <= bottomY || eye.Y > topY) return false;
        var dx = eye.X - (centerX + 0.5f);
        var dz = eye.Z - (centerZ + 0.5f);
        return dx * dx + dz * dz <= outerRadius * outerRadius;
    }

    /// <summary>Whether floor <paramref name="floor"/>'s piles may wake: the ground floor always, a higher one only
    /// once the door below it is open.</summary>
    public static bool FloorAwake(int floor, bool previousDoorOpen) => floor == 0 || previousDoorOpen;

    /// <summary>
    /// Whether floor <paramref name="floor"/>'s door may open: the floor is awake, none of its piles has a wave left and
    /// none of its skeletons is <paramref name="alive"/> (living, owed back after a despawn, or pending a save restore).
    /// </summary>
    public static bool DoorMayOpen(int floor, bool previousDoorOpen, int pilesLeft, int alive) =>
        FloorAwake(floor, previousDoorOpen) && pilesLeft == 0 && alive == 0;

    /// <summary>
    /// The body centers of up to <paramref name="count"/> skeletons a pile at (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="z"/>) raises: the pile's own column first, then the four sides, then the diagonals, keeping only
    /// the columns whose cells at <paramref name="y"/> and <paramref name="y"/> + 1 are open (<paramref name="isOpen"/>).
    /// </summary>
    public static List<Vector3> SpawnPoints(int x, int y, int z, int count, Func<int, int, int, bool> isOpen)
    {
        var points = new List<Vector3>(Math.Max(0, Math.Min(count, SpawnColumns.Length)));
        for (var i = 0; i < SpawnColumns.Length && points.Count < count; i++)
        {
            var cx = x + SpawnColumns[i].Dx;
            var cz = z + SpawnColumns[i].Dz;
            if (!isOpen(cx, y, cz) || !isOpen(cx, y + 1, cz)) continue;
            points.Add(new Vector3(cx + 0.5f, y + MobConfig.SkeletonHeight * 0.5f, cz + 0.5f));
        }
        return points;
    }

    /// <summary>
    /// What a right-click on <paramref name="record"/>'s altar comes to: a killed king's tower is silent for good, a
    /// waking or awake one says so, and otherwise the ritual may begin only once every floor's door is open.
    /// </summary>
    public static AltarVerdict AltarVerdictOf(TowerRecord record)
    {
        switch (record.AltarState)
        {
            case TowerAltarState.Defeated: return AltarVerdict.Defeated;
            case TowerAltarState.Awake: return AltarVerdict.Awake;
            case TowerAltarState.Ritual: return AltarVerdict.Stirring;
        }
        foreach (var floor in record.Floors)
            if (!floor.DoorOpen) return AltarVerdict.Sealed;
        return AltarVerdict.Begin;
    }

    /// <summary>Whether a tower whose altar is <paramref name="altar"/> should be collapsing into its ruin (BT-8): its
    /// Skeleton King was killed and its collapse is not <paramref name="done"/> yet.</summary>
    public static bool CollapseDue(TowerAltarState altar, bool done) => altar == TowerAltarState.Defeated && !done;

    /// <summary>Whether a bound skeleton at <paramref name="position"/> strayed more than <paramref name="leash"/>
    /// blocks horizontally from its tower's center column.</summary>
    public static bool Strayed(Vector3 position, int centerX, int centerZ, float leash)
    {
        var dx = position.X - (centerX + 0.5f);
        var dz = position.Z - (centerZ + 0.5f);
        return dx * dx + dz * dz > leash * leash;
    }
}
