using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Models;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Data;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Particles;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.Chunks;
using VoxelEngine.UI;

namespace VoxelEngine.Graphics;

/// <summary>
/// Lazily built cube meshes, one per block type. Item drops, held items and inventory
/// icons all draw the same geometry, so they share one cache instead of each keeping a copy.
/// Every one of them is the whole object (<see cref="WholeModel"/>): a block two cells tall shows both halves and
/// a multiblock anchor its whole volume, fitted to one cell, so no consumer can show a single cell of one.
/// (The mining pop used to share it too; it now draws the hitboxes as tile-cropped boxes
/// via PopBoxEmitter so partial blocks keep their texel density.) The break debris asks for
/// <see cref="GetTinted"/> instead, which for a biome-tinted block is a second cube wearing
/// its world tiles; drops, held items and icons keep <see cref="Get"/> and the default
/// colored tiles.
/// </summary>
public class BlockCubeCache : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<ushort, Mesh> _meshes = new();
    private readonly Dictionary<ushort, Mesh> _tintedMeshes = new();
    private readonly Dictionary<ushort, Mesh> _surfaceMeshes = new();
    private readonly Dictionary<ushort, Mesh> _lookMeshes = new();
    private readonly Dictionary<(ushort, int), Mesh> _pileMeshes = new();
    private Mesh? _colorChip;

    public BlockCubeCache(GL gl) => _gl = gl;

    /// <summary>The cube for a block type, the whole object for a multi-cell one (<see cref="WholeModel"/>), or
    /// null for air and unknown ids.</summary>
    public Mesh? Get(ushort blockId)
    {
        if (_meshes.TryGetValue(blockId, out var mesh))
            return mesh;

        var type = BlockType.ById[blockId];
        if (type == null) return null;

        // Every registered block is baked at startup; the plain cube is only a safety net so a
        // half-initialized registry still draws something instead of throwing
        if (type.BakedModel == null)
        {
            mesh = CreateFallbackCube(type);
        }
        else
        {
            var whole = WholeModel(type);
            mesh = Primitives.CreateBlockModel(_gl, whole.Quads, whole.Size, whole.Center);
        }
        _meshes[blockId] = mesh;
        return mesh;
    }

    /// <summary>
    /// The quads of the whole object a block stands for, in the block's own cell space, with the scale and the
    /// center that fit it to the one-cell footprint every plain block's cube has. A multiblock anchor's model spans
    /// its whole volume, centered on the volume. The lower half of a two-cell block (a door, a waystone) is both
    /// halves stacked, centered where they meet and, sideways, on the middle of what is drawn, so a thin door spins
    /// about its own panel. Every other block is its model about its cell's vertical center;
    /// <paramref name="visualCenter"/> also centers it sideways on what is drawn (the look-at label, whose shape
    /// spins in place). GL-free.
    /// </summary>
    public static (IReadOnlyList<BakedQuad> Quads, float Size, Vector3 Center) WholeModel(BlockType type,
        bool visualCenter = false)
    {
        var model = type.BakedModel!;
        if (type.Renderer == BlockRenderer.Multiblock)
            return (model.Quads, 1f / type.MultiblockSize, new Vector3(type.MultiblockSize / 2f));

        if (type.UpperHalfId != 0 && BlockType.ById[type.UpperHalfId]?.BakedModel is { } upper)
        {
            BakedQuad[] quads = [.. model.Quads, .. upper.Quads.Select(q => Shifted(q, 1f))];
            return (quads, 0.5f, SidewaysMiddle(quads) with { Y = 1f });
        }

        return (model.Quads, 1f, visualCenter ? SidewaysMiddle(model.Quads) with { Y = 0.5f } : new Vector3(0.5f));
    }

    // The middle of the quads' bounds on X and Z (Y is the caller's)
    private static Vector3 SidewaysMiddle(IReadOnlyList<BakedQuad> quads)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var quad in quads)
        foreach (var position in quad.Positions)
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }
        return quads.Count == 0 ? new Vector3(0.5f) : (min + max) * 0.5f;
    }

    /// <summary>
    /// The mesh the look-at label spins for a block in a given state: a branch pile is its
    /// <see cref="BuildBranchPile"/> sticks (one per state-byte count, so the label shows how many the cell
    /// holds), anything else is <see cref="WholeModel"/> about its visual center, so a shape that does not fill
    /// its cell (a wall torch, a door) still spins in place.
    /// </summary>
    public Mesh? GetLookAt(ushort blockId, byte state)
    {
        if (BlockType.ById[blockId] is not { Renderer: BlockRenderer.BranchPile } type)
            return GetCentered(blockId);

        var count = BranchPileGenerator.CountWithState(state, type.StackMax >= 2 ? type.StackMax : 4);
        var key = (blockId, count);
        if (_pileMeshes.TryGetValue(key, out var mesh))
            return mesh;

        var (vertices, indices) = BuildBranchPile(type, count);
        mesh = new Mesh(_gl, vertices, indices);
        _pileMeshes[key] = mesh;
        return mesh;
    }

    /// <summary>
    /// The crossed sticks of a <paramref name="count"/>-stick branch pile as a standalone model, laid out
    /// by <see cref="StackEmitter.BranchStick"/> like the world's and centered on the origin (the pile's
    /// own height, so it spins in place). Faces are shaded as fully unoccluded. GL-free.
    /// </summary>
    public static (Vertex[] Vertices, ushort[] Indices) BuildBranchPile(BlockType type, int count)
    {
        var vertices = new List<Vertex>(count * 6 * 4);
        var indices = new List<ushort>(count * 6 * 6);
        var center = new Vector3(0.5f, BranchPileGenerator.PileTop(count) * 0.5f, 0.5f);
        var variant = BranchPileGenerator.VariantAt(0, 0);
        for (var b = 0; b < count; b++)
        {
            StackEmitter.BranchStick(0, 0, variant, b, out var min, out var max, out var cx, out var cz, out var cos, out var sin);
            for (var face = 0; face < 6; face++)
            {
                var tile = face is 2 or 3 ? type.Top : type.Side;
                ChunkFaceTables.BoxFaceCorners(min, max, face, out var p0, out var p1, out var p2, out var p3);
                var normal = Chunk.CubeFaces[face].normal;
                var shade = ChunkFaceTables.ShadeLut[(face << 2) | 3];
                var offset = (ushort)vertices.Count;
                Span<Vector3> corners = [p0, p1, p2, p3];
                foreach (var corner in corners)
                    vertices.Add(new Vertex(StackEmitter.SpinY(corner, cx, cz, cos, sin) - center, shade, shade, shade, 255,
                        ChunkFaceTables.CropUv(face, corner), tile, 1f, 0f, StackEmitter.SpinY(normal, 0f, 0f, cos, sin)));
                indices.AddRange([offset, (ushort)(offset + 1), (ushort)(offset + 2), offset, (ushort)(offset + 2), (ushort)(offset + 3)]);
            }
        }
        return (vertices.ToArray(), indices.ToArray());
    }

    // Get's mesh when the block already sits in the middle of its cell, else its own about the visual center
    private Mesh? GetCentered(ushort blockId)
    {
        if (BlockType.ById[blockId] is not { BakedModel: not null } type) return Get(blockId);
        var whole = WholeModel(type, visualCenter: true);
        if (whole.Center == WholeModel(type).Center) return Get(blockId);
        if (_lookMeshes.TryGetValue(blockId, out var mesh))
            return mesh;

        mesh = Primitives.CreateBlockModel(_gl, whole.Quads, whole.Size, whole.Center);
        _lookMeshes[blockId] = mesh;
        return mesh;
    }

    private static BakedQuad Shifted(BakedQuad quad, float dy) =>
        quad with { Positions = quad.Positions.Select(p => p + new Vector3(0f, dy, 0f)).ToArray() };

    /// <summary>
    /// The cube a standalone world copy of the block draws (the break debris): the same as
    /// <see cref="Get"/> for an untinted block, and for a biome-tinted one the cube built by
    /// <see cref="BuildTintedCube"/>, whose tinted quads take their color from the plain
    /// path's per-draw <c>uBiomeTint</c>, so one cached mesh serves every cell.
    /// </summary>
    public Mesh? GetTinted(ushort blockId)
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
    public Mesh? GetSurface(ushort blockId)
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
    /// <see cref="BuildColorChip"/> over <see cref="JuiceConfig.ColorChipTile"/>, painted per
    /// draw by <c>uBiomeTint</c>.
    /// </summary>
    public Mesh GetColorChip()
    {
        if (_colorChip != null) return _colorChip;
        var (vertices, indices) = BuildColorChip(TextureAtlas.TileOf(JuiceConfig.ColorChipTile));
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
            faceShades[face] = ChunkFaceTables.ShadeLut[(face << 2) | 3] / 255f;
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
        foreach (var mesh in _lookMeshes.Values)
            mesh.Dispose();
        _lookMeshes.Clear();
        foreach (var mesh in _pileMeshes.Values)
            mesh.Dispose();
        _pileMeshes.Clear();
        foreach (var mesh in _surfaceMeshes.Values)
            mesh.Dispose();
        _surfaceMeshes.Clear();
        _colorChip?.Dispose();
        _colorChip = null;

        GC.SuppressFinalize(this);
    }
}
