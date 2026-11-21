using System;
using VoxelEngine.Chunks;

namespace VoxelEngine.Lod;

/// <summary>
/// One main-thread coverage adapter for all tree draw passes. A finer tile replaces a
/// coarse tree only when its own result is empty, its fade has finished, or a still
/// finer/full-detail result completely covers the same tile and Y interval.
/// </summary>
internal sealed class LodTreeCoverPass(LodRegionSet regions)
{
    internal void Update(ChunkManager chunks) =>
        Update((cx, cz, minY, maxY) => FullY(chunks, cx, cz, minY, maxY));

    // The callback is the exact production full-detail readiness seam. Tests can
    // supply explicit chunk draw states without initializing a world or renderer.
    internal void Update(Func<int, int, int, int, bool> fullReady)
    {
        foreach (var region in regions.RegionList)
            UpdateRegion(region, fullReady);
    }

    private void UpdateRegion(LodRegion region, Func<int, int, int, int, bool> fullReady)
    {
        if (region.TreeSourceTiles.Length == 0) return;
        var side = region.SizeBlocks / Chunk.Size;
        Span<bool> supplied = stackalloc bool[256];
        Span<bool> child = stackalloc bool[256];
        Span<bool> full = stackalloc bool[256];
        for (var x = 0; x < side; x++)
        for (var z = 0; z < side; z++)
        {
            var i = x * side + z;
            supplied[i] = !region.HasTreeInstances ||
                (region.TreeSourceTiles[i >> 6] & (1UL << (i & 63))) == 0 ||
                region.TreeYMin[i] >= region.TreeYMax[i];
            if (supplied[i]) continue;
            var cx = region.X * side + x;
            var cz = region.Z * side + z;
            if (region.Level == 0)
                full[i] = fullReady(cx, cz, region.TreeYMin[i], region.TreeYMax[i]);
            else
                child[i] = ReadyAt(region.Level - 1, cx, cz,
                    region.TreeYMin[i], region.TreeYMax[i], fullReady);
        }
        var coverage = LodTreeCoverage.Build(region.SizeBlocks, region.Level,
            supplied[..(side * side)], child[..(side * side)], full[..(side * side)]);
        region.SetTreeCoverage(coverage.Words);
    }

    private bool ReadyAt(int level, int cx, int cz, int minY, int maxY,
        Func<int, int, int, int, bool> fullReady)
    {
        var side = LodRegion.Levels[level].RegionBlocks / Chunk.Size;
        var key = (level, MathUtils.FloorDiv(cx, side), MathUtils.FloorDiv(cz, side));
        if (!regions.Regions.TryGetValue(key, out var finer)) return false;
        if (!finer.HasTreeInstances || finer.TreeSourceTiles.Length == 0) return true;
        var x = cx - finer.X * side;
        var z = cz - finer.Z * side;
        var i = x * side + z;
        if (finer.TreeYMin[i] >= finer.TreeYMax[i] ||
            (finer.TreeSourceTiles[i >> 6] & (1UL << (i & 63))) == 0 ||
            finer.Lifetime >= LodRegion.MAX_LIFETIME)
            return true;
        return level == 0
            ? fullReady(cx, cz, minY, maxY)
            : ReadyAt(level - 1, cx, cz, minY, maxY, fullReady);
    }

    private static bool FullY(ChunkManager chunks, int cx, int cz, int minY, int maxY)
    {
        if (minY >= maxY) return true;
        var first = minY >> Chunk.SizeShift;
        var last = (maxY - 1) >> Chunk.SizeShift;
        if ((long)last - first + 1 > 128) return false;
        for (var cy = first; cy <= last; cy++)
        {
            if (!chunks.Chunks.TryGetValue(cx, cy, cz, out var chunk) || !chunk.IsMeshed)
                return false;
            if ((chunk.HasMesh || chunk.HasTranslucentMesh) &&
                chunk.lifetime < Chunk.MAX_LIFETIME)
                return false;
        }
        return true;
    }
}
