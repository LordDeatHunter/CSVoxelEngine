using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Lazily built cube meshes, one per block type. Item drops, held items and inventory
/// icons all draw the same geometry, so they share one cache instead of each keeping a copy.
/// (The mining pop used to share it too; it now draws the hitboxes as tile-cropped boxes
/// via PopBoxEmitter so partial blocks keep their texel density.) The break debris asks for
/// <see cref="GetTinted"/> instead, which for a biome-tinted block is a second cube wearing
/// its world tiles; drops, held items and icons keep <see cref="Get"/> and the default
/// coloured tiles.
/// </summary>
public class BlockCubeCache : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<byte, Mesh> _meshes = new();
    private readonly Dictionary<byte, Mesh> _tintedMeshes = new();

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

    /// <summary>
    /// The cube a standalone world copy of the block draws (the break debris): the same as
    /// <see cref="Get"/> for an untinted block, and for a biome-tinted one the cube built by
    /// <see cref="BuildTintedCube"/>, whose tinted quads take their colour from the plain
    /// path's per-draw <c>uBiomeTint</c>, so one cached mesh serves every cell.
    /// </summary>
    public Mesh? GetTinted(byte blockId)
    {
        var type = BlockType.ById[blockId];
        if (type?.BiomeTint == null) return Get(blockId);

        if (_tintedMeshes.TryGetValue(blockId, out var mesh))
            return mesh;

        var (vertices, indices) = BuildTintedCube(type);
        mesh = new Mesh(_gl, vertices, indices);
        _tintedMeshes[blockId] = mesh;
        return mesh;
    }

    /// <summary>
    /// A biome-tinted block as a unit cube centred on the origin (like
    /// <see cref="Primitives.CreateBlockModel(GL, BakedModel, float)"/>, so the debris model
    /// matrix is unchanged), wearing its <see cref="BiomeTintFaces"/> tiles through the pop
    /// emitter: a BiomeTint block bakes greedy, so it is always a full cube. GL-free.
    /// </summary>
    public static (Vertex[] Vertices, ushort[] Indices) BuildTintedCube(BlockType type)
    {
        var vertices = new List<Vertex>(PopBoxEmitter.MaxQuadsPerBox * 4);
        var indices = new List<ushort>(PopBoxEmitter.MaxQuadsPerBox * 6);
        ushort offset = 0;
        PopBoxEmitter.EmitBox(vertices, indices, ref offset, BlockHitbox.FullCube, BlockHitbox.FullCube,
            new Vector3(-0.5f), type.FaceTiles, type.BiomeTint);
        return (vertices.ToArray(), indices.ToArray());
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
        foreach (var mesh in _tintedMeshes.Values)
            mesh.Dispose();
        _tintedMeshes.Clear();

        GC.SuppressFinalize(this);
    }
}
