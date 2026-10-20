using System;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Lod;

/// <summary>
/// The far LOD's buried-flag pass: flags every region a loaded finer level or the meshed full-detail
/// chunks already cover, so one LOD level is drawn per area, re-running only when its inputs move and
/// spreading the level-0 cover tests over frames. Main thread only; <see cref="LodManager.Update"/> runs it last.
/// </summary>
internal sealed class LodCoverPass(LodRegionSet regionSet)
{
    private readonly LodRegionSet _regionSet = regionSet;

    // Inputs of the last buried-flag pass; see Update. -1 / null force the first pass.
    private int _lastBuriedMeshVersion = -1;
    private int _lastBuriedRegionSetVersion = -1;
    private Vector3? _lastBuriedCamChunk;

    // Round-robin cursor into RegionList for the unburied level-0 slice, and whether
    // the slice has lapped it since the inputs last changed. While false the pass runs
    // every frame, so a region added at rest still converges instead of waiting for
    // the next input change that may never come.
    private int _coverCursor;
    private bool _coverConverged = true;

    /// <summary>
    /// Refreshes buried flags when their inputs moved, spreading the expensive unburied
    /// level-0 cover tests over frames (see <see cref="UpdateBuriedFlags"/>). Headless-safe:
    /// it needs only the region list and a chunk manager, so tests drive it directly.
    /// </summary>
    internal void UpdateCoverFlags(ChunkManager chunkManager, Vector3 camChunk)
    {
        if (chunkManager.MeshVersion != _lastBuriedMeshVersion
            || _regionSet.Version != _lastBuriedRegionSetVersion
            || camChunk != _lastBuriedCamChunk)
        {
            _lastBuriedMeshVersion = chunkManager.MeshVersion;
            _lastBuriedRegionSetVersion = _regionSet.Version;
            _lastBuriedCamChunk = camChunk;
            _coverConverged = false;
        }

        if (!_coverConverged)
            _coverConverged = UpdateBuriedFlags(chunkManager, camChunk);
    }

    /// <summary>The cover half of <see cref="LodManager.ReleaseAll"/>: leaves the pass converged with its cursor at the start. Main thread only.</summary>
    internal void ResetCover()
    {
        _coverConverged = true;
        _coverCursor = 0;
    }

    /// <summary>
    /// Flags every region whose whole footprint is already covered by loaded finer-level
    /// regions so the renderer draws a single LOD level per area. The levels are nested
    /// shells stacked below one another: drawing all of them is invisible from above the
    /// terrain (the depth buffer sorts it out) but shows up as layered surfaces once the
    /// camera goes underground. The finest level is likewise flagged where the full-detail
    /// chunks are drawn, because caves cut into the band its shell occupies and would show it.
    ///
    /// Coverage is tested against the regions and chunks that actually exist rather than each
    /// level's radius, so a finer region or chunk that has not been built yet can never open a
    /// hole in the horizon — the coarser mesh keeps drawing until its replacement is loaded.
    /// Runs only when a chunk was meshed or unloaded, a region was added or removed, or the
    /// camera changed chunk; see <see cref="LodManager.Update"/>.
    /// </summary>
    private bool UpdateBuriedFlags(ChunkManager chunkManager, Vector3 camChunk)
    {
        // New regions first: they start unburied and the uploads cap additions per
        // frame below the slice budget, so same-frame evaluation is preserved.
        var budget = StreamingConfig.MaxLodCoverTestsPerFrame;
        foreach (var region in _regionSet.PendingCover)
        {
            region.IsBuried = region.Level > 0
                ? _regionSet.IsCoveredByFinerLevel(region.Level, region.X, region.Z)
                : IsCoveredByChunks(region, chunkManager, camChunk);
            if (region.Level == 0)
                budget--;
            region.PendingCoverIndex = -1;
        }

        // Every entry is leaving the list, so every index must go back to -1 here. Without this
        // a region added in one pass and removed after a later one would swap-remove against a
        // stale index and corrupt whichever region happens to sit there now.
        _regionSet.PendingCover.Clear();

        // Coarser levels cost four lookups each and a stale buried flag is a hole in
        // the horizon, so both run every pass. Only stale-unburied is harmless
        // (overdraw the depth buffer already hides from above), which is what the
        // bounded slice below spends its budget on.
        for (var i = 0; i < _regionSet.RegionList.Count; i++)
        {
            var region = _regionSet.RegionList[i];
            if (region.Level > 0 || region.IsBuried)
                region.IsBuried = region.Level > 0
                    ? _regionSet.IsCoveredByFinerLevel(region.Level, region.X, region.Z)
                    : IsCoveredByChunks(region, chunkManager, camChunk);
        }

        if (_regionSet.RegionList.Count == 0)
            return true;

        // Round-robin over the list; swap-removal may skip or repeat an entry when the
        // list changes mid-cycle, which only ever delays overdraw, never a hole.
        if (_coverCursor >= _regionSet.RegionList.Count || _coverCursor < 0)
            _coverCursor = _regionSet.RegionList.Count - 1;
        var idx = _coverCursor;
        for (var visited = 0; visited < _regionSet.RegionList.Count; visited++)
        {
            var region = _regionSet.RegionList[idx];
            if (region.Level == 0 && !region.IsBuried)
            {
                if (budget <= 0)
                {
                    _coverCursor = idx;
                    return false;
                }
                region.IsBuried = IsCoveredByChunks(region, chunkManager, camChunk);
                budget--;
            }
            if (--idx < 0)
                idx = _regionSet.RegionList.Count - 1;
        }
        _coverCursor = idx;
        return true;
    }

    /// <summary>
    /// Whether every chunk this region's shell could show through is loaded and drawn. The
    /// chunk ranges come from the region build (<see cref="LodRegion.ColumnCyMin"/>); presence
    /// is tested per chunk rather than by distance because the load volume is a sphere, so
    /// terrain far below the camera near its horizontal edge is not loaded.
    /// </summary>
    private static bool IsCoveredByChunks(LodRegion region, ChunkManager chunkManager, Vector3 camChunk)
    {
        var span = region.SizeBlocks / Chunk.Size;
        var baseCx = region.X * span;
        var baseCz = region.Z * span;

        // Chunks only exist within ChunkRadius of the camera, so a region whose nearest chunk
        // column is farther than that cannot be covered; skips the lookups for most of the ring
        var dx = Math.Max(0, Math.Max(baseCx - (int)camChunk.X, (int)camChunk.X - (baseCx + span - 1)));
        var dz = Math.Max(0, Math.Max(baseCz - (int)camChunk.Z, (int)camChunk.Z - (baseCz + span - 1)));
        if (dx * dx + dz * dz > StreamingConfig.ChunkRadiusSq)
            return false;

        for (var ci = 0; ci < span; ci++)
        for (var cj = 0; cj < span; cj++)
        {
            var c = ci * span + cj;
            for (var cy = region.ColumnCyMin[c]; cy <= region.ColumnCyMax[c]; cy++)
            {
                if (!chunkManager.Chunks.TryGetValue(baseCx + ci, cy, baseCz + cj, out var chunk)
                    || !chunk.IsMeshed)
                    return false;
            }
        }

        return true;
    }
}
