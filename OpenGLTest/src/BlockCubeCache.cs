using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Lazily built cube meshes, one per block type. Item drops, held items and inventory
/// icons all draw the same geometry, so they share one cache instead of each keeping a copy.
/// (The mining pop used to share it too; it now draws the hitboxes as tile-cropped boxes
/// via PopBoxEmitter so partial blocks keep their texel density.)
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

        // Every registered block is baked at startup; the plain cube is only a safety net so a
        // half-initialised registry still draws something instead of throwing
        mesh = type.BakedModel != null
            ? Primitives.CreateBlockModel(_gl, type.BakedModel)
            : CreateFallbackCube(type);
        _meshes[blockId] = mesh;
        return mesh;
    }

    /// <summary>Face tiles on a plain unit cube, shaded as fully unoccluded.</summary>
    private Mesh CreateFallbackCube(BlockType type)
    {
        Span<UvRect> faceUvs = stackalloc UvRect[6];
        Span<float> faceShades = stackalloc float[6];
        for (var face = 0; face < 6; face++)
        {
            faceUvs[face] = UvRect.Tile(type.FaceTiles[face]);
            faceShades[face] = Chunk.ShadeLut[(face << 2) | 3] / 255f;
        }

        return Primitives.CreateCube(_gl, 1f, Rgba.White, faceUvs, faceShades);
    }

    public void Dispose()
    {
        foreach (var mesh in _meshes.Values)
            mesh.Dispose();
        _meshes.Clear();

        GC.SuppressFinalize(this);
    }
}
