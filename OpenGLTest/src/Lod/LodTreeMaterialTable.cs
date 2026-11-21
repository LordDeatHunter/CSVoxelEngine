using System;
using System.Collections.Generic;

namespace VoxelEngine.Lod;

/// <summary>Build-local material sharing by actual ushort block identity, never by root count.</summary>
internal sealed class LodTreeMaterialTable
{
    private readonly Dictionary<ushort, int> _byBlock = new();
    private readonly List<LodTreeMaterial> _materials = [];

    internal int Count => _materials.Count;
    internal IReadOnlyList<LodTreeMaterial> Materials => _materials;

    internal int Add(ushort blockId, LodTreeMaterial material)
    {
        if (_byBlock.TryGetValue(blockId, out var index))
        {
            if (!material.Equals(_materials[index]))
                throw new InvalidOperationException("One block ID resolved to different tree materials in one build.");
            return index;
        }
        if (_materials.Count > ushort.MaxValue)
            throw new InvalidOperationException("The shared ushort block ID space has been exceeded.");
        index = _materials.Count;
        _materials.Add(material);
        _byBlock.Add(blockId, index);
        return index;
    }
}
