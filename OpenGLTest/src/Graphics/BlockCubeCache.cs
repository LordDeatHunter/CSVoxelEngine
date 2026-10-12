using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Models;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Data;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Particles;
using VoxelEngine.UI;

namespace VoxelEngine.Graphics;

/// <summary>
/// Lazily built cube meshes, one per block type. Item drops, held items and inventory
/// icons all draw the same geometry, so they share one cache instead of each keeping a copy.
/// (The mining pop used to share it too; it now draws the hitboxes as tile-cropped boxes
/// via PopBoxEmitter so partial blocks keep their texel density.) The break debris asks for
/// <see cref="GetTinted"/> instead, which for a biome-tinted block is a second cube wearing
/// its world tiles; drops, held items and icons keep <see cref="Get"/> and the default
/// colored tiles.
/// </summary>
public class BlockCubeCache : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<byte, Mesh> _meshes = new();
    private readonly Dictionary<byte, Mesh> _tintedMeshes = new();
    private readonly Dictionary<byte, Mesh> _surfaceMeshes = new();
    private Mesh? _colorChip;

    public BlockCubeCache(GL gl) => _gl = gl;

    /// <summary>The cube for a block type, or null for air and unknown ids.</summary>
    public Mesh? Get(byte blockId)
    {
        if (_meshes.TryGetValue(blockId, out var mesh))
            return mesh;

        var type = BlockType.ById[blockId];
        if (type == null) return null;

        // Every registered block is baked at startup; the plain cube is only a safety net so a
        // half-initialized registry still draws something instead of throwing
        mesh = type.BakedModel != null
            ? Primitives.CreateBlockModel(_gl, type.BakedModel)
            : CreateFallbackCube(type);
        _meshes[blockId] = mesh;
        return mesh;
    }

    /// <summary>
    /// The cube a standalone world copy of the block draws (the break debris): the same as
    /// <see cref="Get"/> for an untinted block, and for a biome-tinted one the cube built by
    /// <see cref="BuildTintedCube"/>, whose tinted quads take their color from the plain
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
    /// A biome-tinted block as a unit cube centered on the origin (like
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

    /// <summary>
    /// The cube a chip scuffed off the block's surface draws (<see cref="DebrisLook.Surface"/>, the
    /// landing and footstep dust): <see cref="BuildSurfaceCube"/>, one cached mesh per block whose
    /// biome-tinted quads, like <see cref="GetTinted"/>'s, take the per-draw <c>uBiomeTint</c>.
    /// Null for air, unknown ids and a block whose tiles were never baked.
    /// </summary>
    public Mesh? GetSurface(byte blockId)
    {
        if (_surfaceMeshes.TryGetValue(blockId, out var mesh))
            return mesh;

        var type = BlockType.ById[blockId];
        if (type == null || type.FaceTiles.Length < 6) return null;

        var (vertices, indices) = BuildSurfaceCube(type);
        mesh = new Mesh(_gl, vertices, indices);
        _surfaceMeshes[blockId] = mesh;
        return mesh;
    }

    /// <summary>
    /// The block's top face on all six sides of a unit cube centered on the origin, with its tinted
    /// top quad too when the block is biome-tinted, so a chip of grass is green all round instead of
    /// showing the dirt sides a broken-off cube wears. GL-free.
    /// </summary>
    public static (Vertex[] Vertices, ushort[] Indices) BuildSurfaceCube(BlockType type)
    {
        const int top = 4; // +Y in Chunk.CubeFaces order
        var tiles = new ushort[6];
        Array.Fill(tiles, type.FaceTiles[top]);
        BiomeTintFaces? tint = null;
        if (type.BiomeTint is { } bt)
        {
            var baseTiles = new ushort[6];
            var tintedTiles = new ushort[6];
            Array.Fill(baseTiles, bt.Base[top]);
            Array.Fill(tintedTiles, bt.Tinted[top]);
            tint = new BiomeTintFaces(bt.Palette, baseTiles, tintedTiles);
        }

        var vertices = new List<Vertex>(PopBoxEmitter.MaxQuadsPerBox * 4);
        var indices = new List<ushort>(PopBoxEmitter.MaxQuadsPerBox * 6);
        ushort offset = 0;
        PopBoxEmitter.EmitBox(vertices, indices, ref offset, BlockHitbox.FullCube, BlockHitbox.FullCube,
            new Vector3(-0.5f), tiles, tint);
        return (vertices.ToArray(), indices.ToArray());
    }

    /// <summary>
    /// The one cube every plain-color chip draws (<see cref="DebrisLook.Color"/>, a slime's goo):
    /// <see cref="BuildColorChip"/> over <see cref="WorldGenerationConfig.ColorChipTile"/>, painted per
    /// draw by <c>uBiomeTint</c>.
    /// </summary>
    public Mesh GetColorChip()
    {
        if (_colorChip != null) return _colorChip;
        var (vertices, indices) = BuildColorChip(TextureAtlas.TileOf(WorldGenerationConfig.ColorChipTile));
        _colorChip = new Mesh(_gl, vertices, indices);
        return _colorChip;
    }

    /// <summary>
    /// A unit cube centered on the origin wearing <paramref name="grayTile"/> on every face, every quad
    /// flagged biome-tinted, so the plain path's <c>uBiomeTint</c> paints the whole cube one color.
    /// GL-free.
    /// </summary>
    public static (Vertex[] Vertices, ushort[] Indices) BuildColorChip(ushort grayTile)
    {
        var tiles = new ushort[6];
        Array.Fill(tiles, grayTile);
        var baseTiles = new ushort[6];
        Array.Fill(baseTiles, BiomeTintFaces.NoTile);
        var tint = new BiomeTintFaces(BiomeTintPalette.Grass, baseTiles, tiles);

        var vertices = new List<Vertex>(6 * 4);
        var indices = new List<ushort>(6 * 6);
        ushort offset = 0;
        PopBoxEmitter.EmitBox(vertices, indices, ref offset, BlockHitbox.FullCube, BlockHitbox.FullCube,
            new Vector3(-0.5f), tiles, tint);
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
        foreach (var mesh in _surfaceMeshes.Values)
            mesh.Dispose();
        _surfaceMeshes.Clear();
        _colorChip?.Dispose();
        _colorChip = null;

        GC.SuppressFinalize(this);
    }
}
