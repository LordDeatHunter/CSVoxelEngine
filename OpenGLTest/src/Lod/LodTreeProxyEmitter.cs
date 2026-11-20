using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.Lod;

/// <summary>Collects whole compact trees, source-owned tiles, and finite tile Y bounds.</summary>
internal sealed class LodTreeProxyEmitter
{
    private static readonly Func<int, int, LodTreeProxy?> ResolveFar = static (x, z) =>
        TreeGenerator.TryGetTree(x, z, out var species, out var y) && species.HasLeaves
            ? LodTreeProxy.FromFar(species, x, z, y) : null;
    private readonly LodLevelSpec _spec;
    private readonly IWorldSource _source;
    private readonly bool[] _supplied;
    private readonly Func<int, int, LodTreeAnchor.HeightColumn> _anchorColumnAt;
    private readonly int _x0, _z0, _baseCx, _baseCz, _prefetchSpan, _ownedSpan;
    private readonly ulong[] _generated;
    private readonly int[] _yMin, _yMax;
    private readonly LodTreeMaterialTable _materials = new();
    private readonly List<LodTreeInstance> _instances = [];
    private Dictionary<(int X, int Z), bool>? _outsideSupply;

    internal LodTreeProxyEmitter(LodLevelSpec spec, IWorldSource source, bool[] supplied,
        Func<int, int, LodTreeAnchor.HeightColumn> anchorColumnAt,
        int baseFineX, int baseFineZ, int baseChunkX, int baseChunkZ,
        int chunkSpan, int regionChunkX, int regionChunkZ, int regionChunkSpan)
    {
        _spec = spec; _source = source; _supplied = supplied; _anchorColumnAt = anchorColumnAt;
        _x0 = baseFineX; _z0 = baseFineZ; _baseCx = baseChunkX; _baseCz = baseChunkZ;
        _prefetchSpan = chunkSpan; _ownedSpan = regionChunkSpan;
        _yMin = new int[_ownedSpan * _ownedSpan];
        _yMax = new int[_yMin.Length];
        _generated = new ulong[(_yMin.Length + 63) / 64];
        Array.Fill(_yMin, int.MaxValue);
        Array.Fill(_yMax, int.MinValue);
        for (var x = 0; x < _ownedSpan; x++)
        for (var z = 0; z < _ownedSpan; z++)
        {
            var c = x * _ownedSpan + z;
            if (!supplied[(x + 1) * chunkSpan + z + 1])
                _generated[c >> 6] |= 1UL << (c & 63);
        }
    }

    internal LodTreeBuild Build(ReadOnlySpan<Vector3> grass, ReadOnlySpan<Vector3> foliage,
        out int minY, out int maxY)
    {
        minY = int.MaxValue; maxY = int.MinValue;
        if (LodProxyOwnership.HasGeneratedOwnedColumn(_supplied, _prefetchSpan, _ownedSpan))
        {
            var reach = _spec.ProxyCanopy ? TreeGenerator.ProxyReach : TreeGenerator.ReachHorizontal;
            if (TreeCanopy.HasAdded)
            {
                for (var i = 0; i < _spec.Cells; i++)
                for (var j = 0; j < _spec.Cells; j++)
                {
                    var bx = _x0 + i * _spec.CellSize;
                    var bz = _z0 + j * _spec.CellSize;
                    var top = int.MinValue;
                    var leaf = BlockType.Air;
                    for (var dx = 0; dx < _spec.CellSize; dx++)
                    for (var dz = 0; dz < _spec.CellSize; dz++)
                        if (TreeCanopy.TryGetAdded(bx + dx, bz + dz, out var y, out var id) && y > top)
                        { top = y; leaf = id; }
                    if (top != int.MinValue)
                        TryAdd(new LodTreeProxy(bx, bz, top - 1, top - 1, top + 1,
                            bx, bx + _spec.CellSize, bz, bz + _spec.CellSize, 1, 0, leaf,
                            LodCrownProfile.Column), true);
                }
            }
            if (_spec.ProxyCanopy)
            {
                var roots = new List<LodTreeProxy>();
                LodProxyOwnership.CollectFar(_supplied, _prefetchSpan, _ownedSpan,
                    _x0, _z0, _spec.RegionBlocks, reach, RootSupplied, ResolveFar, roots);
                foreach (var root in roots) TryAddNatural(root);
            }
            else
            {
                for (var cx = TreeGenerator.CellOf(_x0 - reach);
                     cx <= TreeGenerator.CellOf(_x0 + _spec.RegionBlocks - 1 + reach); cx++)
                for (var cz = TreeGenerator.CellOf(_z0 - reach);
                     cz <= TreeGenerator.CellOf(_z0 + _spec.RegionBlocks - 1 + reach); cz++)
                foreach (var shape in TreeBatches.BatchOf(cx, cz).Trees)
                    if (!RootSupplied(shape.X, shape.Z))
                        TryAddNatural(LodTreeProxy.FromGrown(shape));
            }
        }
        foreach (var instance in _instances)
        { minY = Math.Min(minY, instance.BaseY); maxY = Math.Max(maxY, instance.CrownTop); }
        var side = _ownedSpan + 1;
        var tint = _instances.Count == 0 ? null : new LodTreeTintLattice(_ownedSpan,
            grass[..(side * side)], foliage[..(side * side)]);
        return new LodTreeBuild([.. _instances], [.. _materials.Materials], tint,
            _generated, _yMin, _yMax);
    }

    private bool RootSupplied(int x, int z)
    {
        var cx = x >> Chunk.SizeShift; var cz = z >> Chunk.SizeShift;
        var pi = cx - _baseCx; var pj = cz - _baseCz;
        if ((uint)pi < (uint)_prefetchSpan && (uint)pj < (uint)_prefetchSpan)
            return _supplied[pi * _prefetchSpan + pj];
        _outsideSupply ??= new Dictionary<(int X, int Z), bool>();
        if (_outsideSupply.TryGetValue((cx, cz), out var supplied)) return supplied;
        supplied = _source.Supplies(cx, cz);
        _outsideSupply.Add((cx, cz), supplied);
        return supplied;
    }

    private bool Intersects(LodProxyBox box) => box.HasVolume &&
        box.MaxX > _x0 && box.MinX < _x0 + _spec.RegionBlocks &&
        box.MaxZ > _z0 && box.MinZ < _z0 + _spec.RegionBlocks;

    private void TryAddNatural(LodTreeProxy tree)
    {
        if (!Intersects(tree.Trunk()) && (!tree.HasCrown || !Intersects(tree.Crown()))) return;
        for (var x = tree.MinX; x < tree.MaxX; x++)
        for (var z = tree.MinZ; z < tree.MaxZ; z++)
            if (TreeCanopy.IsDisturbed(x, z)) return;
        tree = tree with { BaseY = Math.Min(tree.BaseY,
            LodTreeAnchor.ForRoot(tree.RootX, tree.RootZ, _anchorColumnAt)) };
        TryAdd(tree, false);
    }

    private void TryAdd(LodTreeProxy tree, bool added)
    {
        if (!Intersects(tree.Trunk()) && (!tree.HasCrown || !Intersects(tree.Crown()))) return;
        var wood = LodTreeInstance.NoMaterial;
        var leaf = LodTreeInstance.NoMaterial;
        if (!added)
        {
            if (tree.WoodId >= BlockType.ById.Length || BlockType.ById[tree.WoodId] is not { } woodBlock ||
                !LodTreeMaterial.TryFromBlock(woodBlock, out var woodMaterial)) return;
            wood = _materials.Add(tree.WoodId, woodMaterial);
        }
        if (tree.HasCrown)
        {
            if (tree.LeavesId >= BlockType.ById.Length || BlockType.ById[tree.LeavesId] is not { } leafBlock ||
                !LodTreeMaterial.TryFromBlock(leafBlock, out var leafMaterial)) return;
            leaf = _materials.Add(tree.LeavesId, leafMaterial);
        }
        if (!LodTreeInstance.TryCreate(tree, wood, leaf, _materials.Count, added, out var instance) ||
            !instance.TryRegionProxy(_x0, _z0, out _))
            throw new InvalidOperationException("Admitted tree exceeds the compact LOD contract.");
        _instances.Add(instance);
        if (!added) Mark(tree.Trunk());
        if (tree.HasCrown)
        {
            Mark(tree.Crown());
            if (tree.Profile != LodCrownProfile.Column) Mark(tree.Cap());
        }
    }

    private void Mark(LodProxyBox box)
    {
        if (!Intersects(box)) return;
        var x0 = Math.Clamp((int)MathF.Floor(box.MinX - _x0) >> Chunk.SizeShift, 0, _ownedSpan - 1);
        var x1 = Math.Clamp(((int)MathF.Ceiling(box.MaxX - _x0) - 1) >> Chunk.SizeShift, 0, _ownedSpan - 1);
        var z0 = Math.Clamp((int)MathF.Floor(box.MinZ - _z0) >> Chunk.SizeShift, 0, _ownedSpan - 1);
        var z1 = Math.Clamp(((int)MathF.Ceiling(box.MaxZ - _z0) - 1) >> Chunk.SizeShift, 0, _ownedSpan - 1);
        for (var x = x0; x <= x1; x++)
        for (var z = z0; z <= z1; z++)
        {
            var c = x * _ownedSpan + z;
            if ((_generated[c >> 6] & (1UL << (c & 63))) == 0) continue;
            _yMin[c] = Math.Min(_yMin[c], (int)MathF.Floor(box.MinY));
            _yMax[c] = Math.Max(_yMax[c], (int)MathF.Ceiling(box.MaxY));
        }
    }
}
