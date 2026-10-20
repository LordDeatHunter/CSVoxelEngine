using System.Collections.Concurrent;
using System.Collections.Generic;

namespace VoxelEngine.Lod;

/// <summary>
/// The main-thread half of LodManager's region bookkeeping: the swap-removed RegionList beside the
/// Regions table, the regions the next cover pass must test first, the loaded finer-level child count
/// of every coarse region, and the region-set version the cover pass watches. Main thread only.
/// </summary>
internal sealed class LodRegionSet(ConcurrentDictionary<(int Level, int X, int Z), LodRegion> regions, List<LodRegion> regionList)
{
    /// <summary>LodManager's <see cref="LodManager.Regions"/> table, shared by reference.</summary>
    internal readonly ConcurrentDictionary<(int Level, int X, int Z), LodRegion> Regions = regions;

    /// <summary>LodManager's <see cref="LodManager.RegionList"/>, shared by reference.</summary>
    internal readonly List<LodRegion> RegionList = regionList;

    // Bumped by every add, remove and ReleaseAll; LodCoverPass compares it with the version its last pass saw.
    private int _regionSetVersion;

    // Regions added since the last cover pass. They start unburied, so the next pass
    // tests them first: same-frame evaluation, exactly like the uncapped pass did.
    // Each entry carries its own index back in LodRegion.PendingCoverIndex, so an unload
    // swap-removes instead of scanning (item C12(c)); UpdateBuriedFlags resets those
    // indices when it drains the list.
    private readonly List<LodRegion> _pendingCover = new();

    // How many of a coarse region's finer-level children (at its own key) are currently
    // loaded, keyed by the COARSE region's (Level, X, Z) — i.e. a level-1 region's count is
    // the number of its level-0 children present in Regions, and a level-2 region's count is
    // its level-1 children. Maintained incrementally by AddRegion/RemoveRegion so
    // IsCoveredByFinerLevel becomes a single lookup and integer compare instead of
    // ratio*ratio ContainsKey probes. Main thread only, like RegionList/AddRegion/RemoveRegion.
    private readonly Dictionary<(int Level, int X, int Z), int> _finerChildCounts = new();

    /// <summary>The regions added since the last cover pass (see <c>_pendingCover</c>); <see cref="LodCoverPass"/> drains it.</summary>
    internal List<LodRegion> PendingCover => _pendingCover;

    /// <summary>The region-set version: moves on every add, remove and release, which is what makes the cover pass run.</summary>
    internal int Version => _regionSetVersion;

    /// <summary>Bumps <see cref="Version"/> as every add and remove does; <see cref="LodManager.ReleaseAll"/> calls it once every table is empty. Main thread only.</summary>
    internal void BumpVersion() => _regionSetVersion++;

    /// <summary>The region half of <see cref="LodManager.ReleaseAll"/>, under its precondition: disposes every region and empties Regions, RegionList and the cover bookkeeping. Main thread only (it frees GL slices).</summary>
    internal void ReleaseRegions()
    {
        // Backwards, like Update's unload loop: RemoveRegion swap-removes the last entry into
        // the hole, which a descending walk has already visited
        for (var i = RegionList.Count - 1; i >= 0; i--)
        {
            var region = RegionList[i];
            Regions.TryRemove((region.Level, region.X, region.Z), out _);
            RemoveRegion(region);
            region.Dispose();
        }

        // Defensive: a region in the table but not in the list (none should exist)
        foreach (var kvp in Regions)
        {
            if (Regions.TryRemove(kvp.Key, out var stray))
                stray.Dispose();
        }

        Regions.Clear();
        RegionList.Clear();
        _pendingCover.Clear();
        _finerChildCounts.Clear();
    }

    /// <summary>
    /// Registers a region in both the lookup table and <see cref="RegionList"/>.
    /// Main thread only.
    /// </summary>
    internal void AddRegion((int Level, int X, int Z) key, LodRegion region)
    {
        Regions[key] = region;
        region.ListIndex = RegionList.Count;
        RegionList.Add(region);
        region.PendingCoverIndex = _pendingCover.Count;
        _pendingCover.Add(region);
        _regionSetVersion++;

        // Derived from the region, not the caller-supplied key, so this increment and
        // RemoveRegion's decrement read the same source and cannot disagree.
        if (TryParentKey((region.Level, region.X, region.Z), out var parentKey))
            _finerChildCounts[parentKey] = _finerChildCounts.GetValueOrDefault(parentKey) + 1;
    }

    /// <summary>
    /// Swap-removes a region from <see cref="RegionList"/>; a no-op for one that is not in it.
    /// The caller removes it from <see cref="Regions"/> itself, which is where the key lives.
    /// Main thread only.
    /// </summary>
    internal void RemoveRegion(LodRegion region)
    {
        var index = region.ListIndex;
        if (index < 0) return;

        var last = RegionList.Count - 1;
        if (index != last)
        {
            var moved = RegionList[last];
            RegionList[index] = moved;
            moved.ListIndex = index;
        }

        RegionList.RemoveAt(last);
        region.ListIndex = -1;

        // Same swap-remove as above, against _pendingCover (item C12(c)). Honest sizing: this
        // list is drained in the frame it is filled -- AddRegion bumps _regionSetVersion, which
        // forces a UpdateBuriedFlags pass, which Clear()s it unconditionally at the top -- so its
        // length is bounded by MaxLodUploadsPerFrame and the List.Remove this replaces scanned at
        // most four entries. This removes an O(n) a future change could make hot, not a measured
        // cost.
        var pending = region.PendingCoverIndex;
        if (pending >= 0)
        {
            var lastPending = _pendingCover.Count - 1;
            if (pending != lastPending)
            {
                var movedPending = _pendingCover[lastPending];
                _pendingCover[pending] = movedPending;
                movedPending.PendingCoverIndex = pending;
            }

            _pendingCover.RemoveAt(lastPending);
            region.PendingCoverIndex = -1;
        }

        _regionSetVersion++;

        if (TryParentKey((region.Level, region.X, region.Z), out var parentKey))
        {
            var remaining = _finerChildCounts[parentKey] - 1;
            if (remaining <= 0)
                _finerChildCounts.Remove(parentKey);
            else
                _finerChildCounts[parentKey] = remaining;
        }
    }

    /// <summary>
    /// The coarser-level key a region at <paramref name="key"/> feeds its loaded-child count
    /// into, or false for the topmost level, which has no parent. Region grids are aligned at
    /// the origin and sizes are power-of-two multiples of each other, so a child maps onto
    /// its parent by a plain integer division (floored, since region coordinates go negative).
    /// </summary>
    private static bool TryParentKey((int Level, int X, int Z) key, out (int Level, int X, int Z) parentKey)
    {
        if (key.Level >= LodRegion.Levels.Length - 1)
        {
            parentKey = default;
            return false;
        }

        var ratio = LodRegion.Levels[key.Level + 1].RegionBlocks / LodRegion.Levels[key.Level].RegionBlocks;
        parentKey = (key.Level + 1, MathUtils.FloorDiv(key.X, ratio), MathUtils.FloorDiv(key.Z, ratio));
        return true;
    }

    internal bool IsCoveredByFinerLevel(int level, int x, int z)
    {
        // Region grids are aligned at the origin and sizes are power-of-two multiples, so one
        // region maps onto exactly ratio x ratio regions of the finer level: this region is
        // covered once that many of its finer-level children are loaded. AddRegion/RemoveRegion
        // keep _finerChildCounts current, so this is one lookup instead of ratio*ratio probes.
        var finer = level - 1;
        var ratio = LodRegion.Levels[level].RegionBlocks / LodRegion.Levels[finer].RegionBlocks;
        return _finerChildCounts.TryGetValue((level, x, z), out var count) && count == ratio * ratio;
    }
}
