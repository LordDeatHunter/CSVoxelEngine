using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// A column of 4x4 chunks of far terrain, meshed at half resolution (one cell = 2x2x2 blocks)
/// directly from the terrain heightmaps — no block data involved.
///
/// LOD heights take the minimum over a 4x4 fine-column neighborhood and sit one block below
/// the real surface, so the LOD mesh lies strictly inside the real terrain. Wherever
/// full-detail chunks are rendered the depth buffer buries the LOD completely; no masking or
/// camera-dependent remeshing is needed. Player block edits are not reflected here (they only
/// happen within reach, where full-detail terrain always covers the LOD).
/// </summary>
public class LodRegion : IDisposable
{
    public const int ChunkSpan = WorldGenerationConfig.RegionChunkSpan;
    public const int SizeBlocks = ChunkSpan * Chunk.Size; // 64
    private const int CellSize = 2;                       // one LOD cell = 2x2x2 blocks
    private const int Cells = SizeBlocks / CellSize;      // 32 cells per side
    private const int HeightGrid = Cells + 2;             // cells plus 1-cell border for side faces

    public const float MAX_LIFETIME = 0.5f;
    public double Lifetime;

    public readonly int X, Z;      // region grid coordinates
    public readonly float YMin, YMax;

    private readonly GL _gl;
    private Mesh? _mesh;

    public bool HasMesh => _mesh != null;

    // Destroying blocks exposes the LOD buried beneath the surface. These per-column caps
    // force the LOD height below every removed block so the LOD always stays hidden.
    // Keyed by fine (x, z); value is the max fine height LOD may treat as solid.
    private static readonly ConcurrentDictionary<(int X, int Z), int> ColumnHeightCaps = new();

    /// <summary>
    /// Caps a column's LOD height below a removed block. Returns false when the LOD
    /// terrain already sat below the removed block and no region rebuild is needed.
    /// </summary>
    public static bool LowerColumnBelow(int x, int y, int z)
    {
        var noiseHeight = Chunk.GetColumnHeights(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            [((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];

        var current = ColumnHeightCaps.TryGetValue((x, z), out var cap)
            ? Math.Min(cap, noiseHeight)
            : noiseHeight;

        // Removed block was above the LOD-relevant terrain (e.g. a player-placed block)
        if (y > current)
            return false;

        ColumnHeightCaps[(x, z)] = y - 1;
        return true;
    }

    public LodRegion(GL gl, LodMeshData data)
    {
        _gl = gl;
        X = data.X;
        Z = data.Z;
        YMin = data.YMin;
        YMax = data.YMax;
        _mesh = data.Vertices.Count == 0
            ? null
            : new Mesh(gl, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data.Vertices),
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data.Indices));
    }

    public void Render()
    {
        _mesh?.Render();
    }

    public void Dispose()
    {
        _mesh?.Dispose();
        _mesh = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Builds the region mesh on a background thread (CPU only, no GL calls).
    /// Vertices are local in X/Z (relative to the region origin) and absolute in Y.
    /// </summary>
    public static LodMeshData BuildMeshData(int rx, int rz)
    {
        // Prefetch the heightmaps of the 6x6 chunk columns this region samples from
        var baseChunkX = rx * ChunkSpan - 1;
        var baseChunkZ = rz * ChunkSpan - 1;
        var heightmaps = new int[6 * 6][];
        for (var cx = 0; cx < 6; cx++)
        for (var cz = 0; cz < 6; cz++)
            heightmaps[cx * 6 + cz] = Chunk.GetColumnHeights(baseChunkX + cx, baseChunkZ + cz);

        var baseFineX = rx * SizeBlocks;
        var baseFineZ = rz * SizeBlocks;
        var hasCaps = !ColumnHeightCaps.IsEmpty;

        int FineHeight(int fineX, int fineZ)
        {
            var cx = (fineX >> Chunk.SizeShift) - baseChunkX;
            var cz = (fineZ >> Chunk.SizeShift) - baseChunkZ;
            var height = heightmaps[cx * 6 + cz][((fineX & Chunk.SizeMask) << Chunk.SizeShift) | (fineZ & Chunk.SizeMask)];

            if (hasCaps && ColumnHeightCaps.TryGetValue((fineX, fineZ), out var cap) && cap < height)
                height = cap;

            return height;
        }

        // Top solid cell per LOD column (with 1-cell border). Eroded: min height over the
        // surrounding 4x4 fine columns, then one block down, so LOD faces are never coplanar
        // with (or above) real terrain faces.
        var topCell = new int[HeightGrid * HeightGrid];
        for (var i = -1; i <= Cells; i++)
        {
            for (var j = -1; j <= Cells; j++)
            {
                var minHeight = int.MaxValue;
                for (var dx = -1; dx <= 2; dx++)
                for (var dz = -1; dz <= 2; dz++)
                {
                    var h = FineHeight(baseFineX + i * CellSize + dx, baseFineZ + j * CellSize + dz);
                    if (h < minHeight) minHeight = h;
                }

                // Cell cy is solid iff its top (2cy + 1) is at most minHeight - 1
                topCell[(i + 1) * HeightGrid + (j + 1)] = (minHeight - 2) >> 1;
            }
        }

        var grass = BlockType.Types[1];
        var dirt = BlockType.Types[2];

        var vertices = new List<Vertex>(8192);
        var indices = new List<ushort>(12288);
        var yMin = float.MaxValue;
        var yMax = float.MinValue;

        // Horizontal side faces in Chunk.CubeFaces order: -Z, +Z, -X, +X
        Span<int> sideFaces = [0, 1, 2, 3];
        const int topFace = 4;

        void AddQuad(int faceIndex, Vector3 cellOrigin, BlockType.Point tile)
        {
            // Meshes are indexed with ushort; a region can't realistically exceed this,
            // but degrade gracefully instead of overflowing if terrain is extreme
            if (vertices.Count > ushort.MaxValue - 3)
                return;

            var shade = (byte)(Chunk.FaceBrightness[faceIndex] * 255);
            var u0 = tile.X * Chunk.TileUVSize;
            var v0 = tile.Y * Chunk.TileUVSize;
            Span<Vector2> uv =
            [
                new Vector2(u0, v0 + Chunk.TileUVSize),
                new Vector2(u0, v0),
                new Vector2(u0 + Chunk.TileUVSize, v0),
                new Vector2(u0 + Chunk.TileUVSize, v0 + Chunk.TileUVSize)
            ];

            var face = Chunk.CubeFaces[faceIndex].face;
            var indexOffset = (ushort)vertices.Count;
            for (var c = 0; c < 4; c++)
            {
                var pos = cellOrigin + face[c] * CellSize;
                if (pos.Y < yMin) yMin = pos.Y;
                if (pos.Y > yMax) yMax = pos.Y;
                vertices.Add(new Vertex(pos, shade, shade, shade, 255, uv[c]));
            }

            indices.Add((ushort)(indexOffset + 0));
            indices.Add((ushort)(indexOffset + 1));
            indices.Add((ushort)(indexOffset + 2));
            indices.Add((ushort)(indexOffset + 0));
            indices.Add((ushort)(indexOffset + 2));
            indices.Add((ushort)(indexOffset + 3));
        }

        for (var i = 0; i < Cells; i++)
        {
            for (var j = 0; j < Cells; j++)
            {
                var top = topCell[(i + 1) * HeightGrid + (j + 1)];

                AddQuad(topFace, new Vector3(i * CellSize, top * CellSize, j * CellSize), grass.FaceTiles[topFace]);

                foreach (var f in sideFaces)
                {
                    var normal = Chunk.CubeFaces[f].normal;
                    var neighborTop = topCell[(i + 1 + (int)normal.X) * HeightGrid + (j + 1 + (int)normal.Z)];

                    for (var cy = neighborTop + 1; cy <= top; cy++)
                    {
                        var tile = cy == top ? grass.FaceTiles[f] : dirt.FaceTiles[f];
                        AddQuad(f, new Vector3(i * CellSize, cy * CellSize, j * CellSize), tile);
                    }
                }
            }
        }

        return new LodMeshData(rx, rz, vertices, indices, yMin, yMax);
    }
}

public record LodMeshData(int X, int Z, List<Vertex> Vertices, List<ushort> Indices, float YMin, float YMax);
