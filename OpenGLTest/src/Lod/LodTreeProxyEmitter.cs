using System;
using VoxelEngine.Chunks;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Lod;

/// <summary>
/// One region build's finite generated-tree geometry, clipped to region and source ownership.
/// It appends to the terrain emitter's existing opaque mesh and extends burial bounds.
/// </summary>
internal sealed class LodTreeProxyEmitter
{
    private readonly LodLevelSpec _spec;
    private readonly IWorldSource _source;
    private readonly bool[] _supplied;
    private readonly int[] _groundTop;
    private readonly int _heightGrid;
    private readonly int _baseFineX;
    private readonly int _baseFineZ;
    private readonly int _baseChunkX;
    private readonly int _baseChunkZ;
    private readonly int _chunkSpan;
    private readonly int _regionChunkX;
    private readonly int _regionChunkZ;
    private readonly int _regionChunkSpan;
    private readonly int _cellSize;
    private readonly int _cells;

    internal LodTreeProxyEmitter(LodLevelSpec spec, IWorldSource source, bool[] supplied, int[] groundTop,
        int heightGrid, int baseFineX, int baseFineZ, int baseChunkX, int baseChunkZ,
        int chunkSpan, int regionChunkX, int regionChunkZ, int regionChunkSpan)
    {
        _spec = spec;
        _source = source;
        _supplied = supplied;
        _groundTop = groundTop;
        _heightGrid = heightGrid;
        _baseFineX = baseFineX;
        _baseFineZ = baseFineZ;
        _baseChunkX = baseChunkX;
        _baseChunkZ = baseChunkZ;
        _chunkSpan = chunkSpan;
        _regionChunkX = regionChunkX;
        _regionChunkZ = regionChunkZ;
        _regionChunkSpan = regionChunkSpan;
        _cellSize = spec.CellSize;
        _cells = spec.Cells;
    }

    internal void Emit(ref LodFaceEmitter emitter, int[] columnCyMax)
    {
        var reach = _spec.ProxyCanopy ? TreeGenerator.ProxyReach : TreeGenerator.ReachHorizontal;
        var x0 = _baseFineX;
        var z0 = _baseFineZ;
        var x1 = x0 + _spec.RegionBlocks;
        var z1 = z0 + _spec.RegionBlocks;
        // Saved reactive-growth records have only top+leaf per column, not a trunk or owner.
        // Approximate each occupied coarse cell by a two-block-high cap. No file migration.
        if (TreeCanopy.HasAdded)
        {
            for (var i = 0; i < _cells; i++)
            for (var j = 0; j < _cells; j++)
            {
                var bx = x0 + i * _cellSize;
                var bz = z0 + j * _cellSize;
                var top = int.MinValue;
                var leaf = BlockType.Air;
                for (var dx = 0; dx < _cellSize; dx++)
                for (var dz = 0; dz < _cellSize; dz++)
                    if (TreeCanopy.TryGetAdded(bx + dx, bz + dz, out var y, out var id) && y > top)
                    {
                        top = y;
                        leaf = id;
                    }
                if (top == int.MinValue) continue;
                EmitProxyBox(new LodProxyBox(bx, top - 1, bz, bx + _cellSize, top + 1, bz + _cellSize),
                    leaf, true, ref emitter, columnCyMax);
            }
        }

        if (_spec.ProxyCanopy)
        {
            // One cheap placement scan for this region. Far rings never grow a tree.
            for (var x = x0 - reach; x < x1 + reach; x++)
            for (var z = z0 - reach; z < z1 + reach; z++)
                if (TreeGenerator.TryGetTree(x, z, out var species, out var baseY))
                    EmitNaturalProxy(LodTreeProxy.FromFar(species, x, z, baseY), ref emitter, columnCyMax);
        }
        else
        {
            // The near ring uses the same batch-grown shapes as the stamped chunks.
            for (var cx = TreeGenerator.CellOf(x0 - reach); cx <= TreeGenerator.CellOf(x1 - 1 + reach); cx++)
            for (var cz = TreeGenerator.CellOf(z0 - reach); cz <= TreeGenerator.CellOf(z1 - 1 + reach); cz++)
            foreach (var shape in TreeBatches.BatchOf(cx, cz).Trees)
                EmitNaturalProxy(LodTreeProxy.FromGrown(shape), ref emitter, columnCyMax);
        }
    }

    private void EmitNaturalProxy(LodTreeProxy tree, ref LodFaceEmitter emitter, int[] columnCyMax)
    {
        // A supplied root is authoritative even if generated columns beside it are visible.
        // Prefetched columns need no extra source call; only the second chunk of a wide
        // overhang can fall outside the one-column prefetch border.
        var rootCx = tree.RootX >> Chunk.SizeShift;
        var rootCz = tree.RootZ >> Chunk.SizeShift;
        var pi = rootCx - _baseChunkX;
        var pj = rootCz - _baseChunkZ;
        if ((uint)pi < (uint)_chunkSpan && (uint)pj < (uint)_chunkSpan
            ? _supplied[pi * _chunkSpan + pj]
            : _source.Supplies(rootCx, rootCz))
            return;
        // Erosion/height bias lowers the far terrain shell. Anchor the visible wood to
        // that shell when its root lies in this region, so crowns do not float above it.
        if (tree.RootX >= _baseFineX && tree.RootX < _baseFineX + _spec.RegionBlocks
            && tree.RootZ >= _baseFineZ && tree.RootZ < _baseFineZ + _spec.RegionBlocks)
        {
            var i = (tree.RootX - _baseFineX) / _cellSize + 1;
            var j = (tree.RootZ - _baseFineZ) / _cellSize + 1;
            var shellY = (_groundTop[i * _heightGrid + j] + 1) * _cellSize;
            tree = tree with { BaseY = Math.Min(tree.BaseY, shellY) };
        }
        var crown = tree.Crown();
        var trunk = tree.Trunk();
        if (trunk.MaxX <= _baseFineX || trunk.MinX >= _baseFineX + _spec.RegionBlocks
            || trunk.MaxZ <= _baseFineZ || trunk.MinZ >= _baseFineZ + _spec.RegionBlocks)
        {
            if (!tree.HasCrown || crown.MaxX <= _baseFineX || crown.MinX >= _baseFineX + _spec.RegionBlocks
                || crown.MaxZ <= _baseFineZ || crown.MinZ >= _baseFineZ + _spec.RegionBlocks)
                return;
        }

        // Any felled, blasted, mined or decayed canopy column suppresses the whole coarse
        // approximation; it must not resurrect edited leaves in an adjacent LOD cell.
        for (var x = tree.MinX; x < tree.MaxX; x++)
        for (var z = tree.MinZ; z < tree.MaxZ; z++)
            if (TreeCanopy.IsDisturbed(x, z)) return;

        EmitProxyBox(trunk, tree.WoodId, false, ref emitter, columnCyMax);
        if (!tree.HasCrown) return;
        EmitProxyBox(crown, tree.LeavesId, true, ref emitter, columnCyMax);
        if (tree.Profile != VoxelEngine.WorldGen.Trees.Shape.LodCrownProfile.Column)
            EmitProxyBox(tree.Cap(), tree.LeavesId, true, ref emitter, columnCyMax);
    }

    private void EmitProxyBox(LodProxyBox box, ushort blockId, bool leaves,
        ref LodFaceEmitter emitter, int[] columnCyMax)
    {
        if (!box.HasVolume) return;
        var regionX1 = _baseFineX + _spec.RegionBlocks;
        var regionZ1 = _baseFineZ + _spec.RegionBlocks;
        box = box.ClipXZ(_baseFineX, _baseFineZ, regionX1, regionZ1);
        if (!box.HasVolume) return;
        var x0 = box.MinX;
        var x1 = box.MaxX;
        var z0 = box.MinZ;
        var z1 = box.MaxZ;
        var firstCx = (int)MathF.Floor(x0) >> Chunk.SizeShift;
        var lastCx = ((int)MathF.Ceiling(x1) - 1) >> Chunk.SizeShift;
        var firstCz = (int)MathF.Floor(z0) >> Chunk.SizeShift;
        var lastCz = ((int)MathF.Ceiling(z1) - 1) >> Chunk.SizeShift;
        for (var cx = firstCx; cx <= lastCx; cx++)
        for (var cz = firstCz; cz <= lastCz; cz++)
        {
            var k = (cx - _baseChunkX) * _chunkSpan + cz - _baseChunkZ;
            if (_supplied[k]) continue;
            var clipped = box.ClipXZ(cx * Chunk.Size, cz * Chunk.Size,
                (cx + 1) * Chunk.Size, (cz + 1) * Chunk.Size);
            if (!clipped.HasVolume) continue;
            if (!emitter.EmitProxyBox(new LodProxyBox(clipped.MinX - _baseFineX, clipped.MinY,
                clipped.MinZ - _baseFineZ, clipped.MaxX - _baseFineX, clipped.MaxY,
                clipped.MaxZ - _baseFineZ), blockId, leaves))
                continue;
            var ci = cx - _regionChunkX;
            var cj = cz - _regionChunkZ;
            columnCyMax[ci * _regionChunkSpan + cj] = Math.Max(columnCyMax[ci * _regionChunkSpan + cj],
                ((int)MathF.Ceiling(box.MaxY) - 1) >> Chunk.SizeShift);
        }
    }

}
