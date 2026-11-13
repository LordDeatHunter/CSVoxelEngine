using System;
using VoxelEngine.WorldGen.Structures;

namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// The tower map's rules over a player's <see cref="TowerMarks"/>: a use marks the nearest battle tower that is neither
/// marked yet nor conquered (so each map in turn reveals the next one out), searched from worldgen the way
/// <c>/locate tower</c> searches (<see cref="StructureLocator.TryFindTower"/>); a tower whose king falls is forgotten
/// by everyone who marked it. Reads worldgen, so it runs where the seed is: singleplayer or the server, never a client.
/// </summary>
public static class TowerMarking
{
    /// <summary>
    /// Marks the nearest tower to column (<paramref name="x"/>, <paramref name="z"/>) that <paramref name="marks"/> does
    /// not hold and <paramref name="towers"/> does not record as conquered, within
    /// <see cref="WorldGenerationConfig.TowerLocateRings"/> tower cells; false, marking nothing, when there is none.
    /// </summary>
    public static bool TryMarkNearest(TowerMarks marks, float x, float z, TowerManager? towers, out StructureLocation found)
    {
        if (!StructureLocator.TryFindTower((int)MathF.Floor(x), (int)MathF.Floor(z), WorldGenerationConfig.TowerLocateRings,
                out found, (cx, cz) => marks.Contains(cx, cz) || IsConquered(towers, cx, cz)))
            return false;
        marks.Add(found.X, found.Z);
        return true;
    }

    /// <summary>Forgets every marked tower <paramref name="towers"/> records as conquered (a player who was away when it
    /// fell); true when any was.</summary>
    public static bool ForgetConquered(TowerMarks marks, TowerManager towers)
    {
        var changed = false;
        for (var i = marks.Count - 1; i >= 0; i--)
        {
            var (x, z) = marks.Towers[i];
            if (IsConquered(towers, x, z)) changed |= marks.Remove(x, z);
        }
        return changed;
    }

    private static bool IsConquered(TowerManager? towers, int x, int z) => towers?.RecordAt(x, z)?.BossDefeated == true;
}
