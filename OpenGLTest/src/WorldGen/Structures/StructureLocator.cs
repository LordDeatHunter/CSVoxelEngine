using System;
using System.Collections.Generic;

namespace VoxelEngine.WorldGen.Structures;

/// <summary>
/// Finds structures from worldgen alone (<c>/locate</c>): a site is a pure function of its cell and the seed, so no chunk has
/// to be loaded. Only a world holding the real seed may call it, which is singleplayer or a server, never a client (it
/// streams its terrain and holds no seed). Runs on the main thread on demand, never per frame: a candidate's site verdict
/// may build on a cache miss.
/// </summary>
public static class StructureLocator
{
    /// <summary>
    /// The battle tower nearest column (<paramref name="x"/>, <paramref name="z"/>) among the tower cells within
    /// <paramref name="maxRings"/> Chebyshev rings of that column's own cell, or false when none of them holds one. Rolling
    /// every cell of the window is only a hash; the rolled candidates are then judged nearest first, so only those nearer
    /// than the answer pay their cached site verdict. A site <paramref name="isConquered"/> reports true for (its center
    /// column) is skipped: a tower whose king fell is a ruin, no longer a tower to find.
    /// </summary>
    public static bool TryFindTower(int x, int z, int maxRings, out StructureLocation location,
        Func<int, int, bool>? isConquered = null)
    {
        location = default;
        if (!WorldGenerationConfig.TowersEnabled || maxRings < 0) return false;

        var homeX = TowerPlacement.CellOf(x);
        var homeZ = TowerPlacement.CellOf(z);
        var candidates = new List<(double DistanceSquared, int CellX, int CellZ)>();
        for (var cellX = homeX - maxRings; cellX <= homeX + maxRings; cellX++)
        for (var cellZ = homeZ - maxRings; cellZ <= homeZ + maxRings; cellZ++)
        {
            if (!TowerPlacement.TryRoll(cellX, cellZ, out _, out var cx, out var cz, out _)) continue;
            var dx = (double)cx - x;
            var dz = (double)cz - z;
            candidates.Add((dx * dx + dz * dz, cellX, cellZ));
        }

        // Exact, not a heuristic: a site's center is its roll's center (TowerPlacement.Build uses TryRoll's center), so
        // ascending roll distance is ascending site distance and the first accepted site is the nearest. The tuple order
        // is lexicographic, so ties break by cell and the answer is deterministic.
        candidates.Sort();
        foreach (var (_, cellX, cellZ) in candidates)
        {
            if (!TowerPlacement.TryGetSite(cellX, cellZ, out var site)) continue;
            if (isConquered != null && isConquered(site.CenterX, site.CenterZ)) continue;
            location = LocationOf(site, x, z);
            return true;
        }
        return false;
    }

    private static StructureLocation LocationOf(TowerSite site, int x, int z)
    {
        // West of the footprint box at the center row, inside the ring the site rules judged dry and level. It stays inside
        // the tower's own cell (the center keeps its margin from the cell's edges), so it never meets another tower.
        var landX = site.MinX - WorldGenerationConfig.TowerLocateLandingGapBlocks;
        var landZ = site.CenterZ;
        var dx = (double)site.CenterX - x;
        var dz = (double)site.CenterZ - z;
        return new StructureLocation(site.CenterX, site.GroundY + 1, site.CenterZ,
            landX, RuinPlacement.HeightAt(landX, landZ) + 1, landZ, Math.Sqrt(dx * dx + dz * dz));
    }
}
