using System;
using VoxelEngine.Chunks;

namespace VoxelEngine.Lod;

/// <summary>
/// Main-thread source and replacement mask shared by the world, reflection and far shadow.
/// Ground burial is deliberately independent. A faded-in finer proxy or a valid empty finer
/// result can replace a coarse tree; a merely uploaded nonempty child cannot open a hole.
/// </summary>
internal sealed class LodTreeCoverPass(LodRegionSet regions)
{
    private int _meshVersion = -1, _regionVersion = -1, _readyCount = -1;

    internal void Reset()
    {
        _meshVersion = _regionVersion = _readyCount = -1;
    }

    internal void Update(ChunkManager chunks)
    {
        var ready = 0;
        foreach (var region in regions.RegionList)
            if (region.HasTreeInstances && region.Lifetime >= LodRegion.MAX_LIFETIME) ready++;
        if (_meshVersion == chunks.MeshVersion && _regionVersion == regions.Version &&
            _readyCount == ready) return;
        _meshVersion = chunks.MeshVersion;
        _regionVersion = regions.Version;
        _readyCount = ready;
        foreach (var region in regions.RegionList)
            UpdateRegion(region, chunks);
    }

    private void UpdateRegion(LodRegion region, ChunkManager chunks)
    {
        if (region.TreeSourceTiles.Length == 0) return;
        var size = region.SizeBlocks;
        var side = size / Chunk.Size;
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
            {
                full[i] = FullY(chunks, cx, cz, region.TreeYMin[i], region.TreeYMax[i]);
            }
            else
            {
                var finerSize = LodRegion.Levels[region.Level - 1].RegionBlocks;
                var childKey = (region.Level - 1,
                    MathUtils.FloorDiv(cx * Chunk.Size, finerSize),
                    MathUtils.FloorDiv(cz * Chunk.Size, finerSize));
                child[i] = regions.Regions.TryGetValue(childKey, out var finer) &&
                    (!finer.HasTreeInstances || finer.Lifetime >= LodRegion.MAX_LIFETIME);
            }
        }
        var coverage = LodTreeCoverage.Build(size, region.Level,
            supplied[..(side * side)], child[..(side * side)], full[..(side * side)]);
        region.SetTreeCoverage(coverage.Words);
    }

    private static bool FullY(ChunkManager chunks, int cx, int cz, int minY, int maxY)
    {
        if (minY >= maxY) return true;
        var first = minY >> Chunk.SizeShift;
        var last = (maxY - 1) >> Chunk.SizeShift;
        if ((long)last - first + 1 > 128) return false;
        for (var cy = first; cy <= last; cy++)
            if (!chunks.Chunks.TryGetValue(cx, cy, cz, out var chunk) || !chunk.IsMeshed)
                return false;
        return true;
    }
}
