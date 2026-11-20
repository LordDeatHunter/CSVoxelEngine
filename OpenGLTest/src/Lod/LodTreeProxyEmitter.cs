using System;
using System.Collections.Generic;
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
    private static readonly Func<int, int, LodTreeProxy?> ResolveFarProxy = static (x, z) =>
        TreeGenerator.TryGetTree(x, z, out var species, out var baseY) && species.HasLeaves
            ? LodTreeProxy.FromFar(species, x, z, baseY) : null;

    private readonly LodLevelSpec _spec;
    private readonly IWorldSource _source;
    private readonly bool[] _supplied;
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
    private readonly Func<int, int, bool> _rootSupplied;
    private readonly Func<int, int, LodTreeAnchor.HeightColumn> _anchorColumnAt;
    private Dictionary<(int X, int Z), bool>? _outsideSupply;

    internal LodTreeProxyEmitter(LodLevelSpec spec, IWorldSource source, bool[] supplied,
        Func<int, int, LodTreeAnchor.HeightColumn> anchorColumnAt,
        int baseFineX, int baseFineZ, int baseChunkX, int baseChunkZ,
        int chunkSpan, int regionChunkX, int regionChunkZ, int regionChunkSpan)
    {
        _spec = spec;
        _source = source;
        _supplied = supplied;
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
        _rootSupplied = RootSupplied;
        _anchorColumnAt = anchorColumnAt;
    }

    internal void Emit(ref LodFaceEmitter emitter, int[] columnCyMax)
    {
        // A fully supplied region owns no generated tree or saved generated canopy.
        // This precedes either batch growth or the far placement scan.
        if (!LodProxyOwnership.HasGeneratedOwnedColumn(_supplied, _chunkSpan, _regionChunkSpan))
            return;
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
            var roots = new List<LodTreeProxy>();
            LodProxyOwnership.CollectFar(_supplied, _chunkSpan, _regionChunkSpan,
                x0, z0, _spec.RegionBlocks, reach,
                _rootSupplied, ResolveFarProxy, roots);
            foreach (var tree in roots)
                EmitNaturalProxy(tree, ref emitter, columnCyMax);
        }
        else
        {
            // The near ring uses the same batch-grown shapes as the stamped chunks.
            for (var cx = TreeGenerator.CellOf(x0 - reach); cx <= TreeGenerator.CellOf(x1 - 1 + reach); cx++)
            for (var cz = TreeGenerator.CellOf(z0 - reach); cz <= TreeGenerator.CellOf(z1 - 1 + reach); cz++)
            foreach (var shape in TreeBatches.BatchOf(cx, cz).Trees)
                if (shape.Species.HasLeaves && !_rootSupplied(shape.X, shape.Z))
                    EmitNaturalProxy(LodTreeProxy.FromGrown(shape), ref emitter, columnCyMax);
        }
    }

    private bool RootSupplied(int x, int z)
    {
        var cx = x >> Chunk.SizeShift;
        var cz = z >> Chunk.SizeShift;
        var pi = cx - _baseChunkX;
        var pj = cz - _baseChunkZ;
        if ((uint)pi < (uint)_chunkSpan && (uint)pj < (uint)_chunkSpan)
            return _supplied[pi * _chunkSpan + pj];
        _outsideSupply ??= new Dictionary<(int X, int Z), bool>();
        var key = (cx, cz);
        if (_outsideSupply.TryGetValue(key, out var supplied)) return supplied;
        supplied = _source.Supplies(cx, cz);
        _outsideSupply.Add(key, supplied);
        return supplied;
    }

    private void EmitNaturalProxy(LodTreeProxy tree, ref LodFaceEmitter emitter, int[] columnCyMax)
    {
        var crown = tree.Crown();
        var trunk = tree.Trunk();
        var trunkIntersects = !(trunk.MaxX <= _baseFineX || trunk.MinX >= _baseFineX + _spec.RegionBlocks
            || trunk.MaxZ <= _baseFineZ || trunk.MinZ >= _baseFineZ + _spec.RegionBlocks);
        var crownIntersects = tree.HasCrown && !(crown.MaxX <= _baseFineX
            || crown.MinX >= _baseFineX + _spec.RegionBlocks
            || crown.MaxZ <= _baseFineZ || crown.MinZ >= _baseFineZ + _spec.RegionBlocks);
        if (!trunkIntersects && !crownIntersects) return;

        // Any felled, blasted, mined or decayed canopy column suppresses the whole coarse
        // approximation; it must not resurrect edited leaves in an adjacent LOD cell.
        for (var x = tree.MinX; x < tree.MaxX; x++)
        for (var z = tree.MinZ; z < tree.MaxZ; z++)
            if (TreeCanopy.IsDisturbed(x, z)) return;

        if (trunkIntersects)
        {
            // The anchor is a pure function of the root's generated height neighborhood,
            // independent of which adjacent region or LOD level clips this same trunk.
            tree = tree with { BaseY = Math.Min(tree.BaseY,
                LodTreeAnchor.ForRoot(tree.RootX, tree.RootZ, _anchorColumnAt)) };
            EmitProxyBox(tree.Trunk(), tree.WoodId, false, ref emitter, columnCyMax);
        }
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
