using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Lazily built cube meshes, one per block type. The mining pop, item drops and inventory icons
/// all draw the same geometry, so they share one cache instead of each keeping a copy.
/// </summary>
public class BlockCubeCache : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<byte, Mesh> _meshes = new();

    public BlockCubeCache(GL gl) => _gl = gl;

    /// <summary>The cube for a block type, or null for air and unknown ids.</summary>
    public Mesh? Get(byte blockId)
    {
        if (_meshes.TryGetValue(blockId, out var mesh))
            return mesh;

        var type = BlockType.ById[blockId];
        if (type == null) return null;

        mesh = Primitives.CreateBlockCube(_gl, type);
        _meshes[blockId] = mesh;
        return mesh;
    }

    public void Dispose()
    {
        foreach (var mesh in _meshes.Values)
            mesh.Dispose();
        _meshes.Clear();

        GC.SuppressFinalize(this);
    }
}
