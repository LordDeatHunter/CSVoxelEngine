using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Parameters for one far-terrain LOD ring. RegionBlocks and RegionRadius must keep
/// CellSize * (RegionBlocks / CellSize) == RegionBlocks with power-of-two sizes.
/// </summary>
public readonly record struct LodLevelSpec(int CellSize, int RegionBlocks, int RegionRadius, int SampleMargin, int HeightBias)
{
    public int Cells => RegionBlocks / CellSize;
}

/// <summary>
/// A column of far terrain meshed at reduced resolution directly from the terrain
/// heightmaps — no block data involved.
///
/// LOD heights take the minimum over a widened fine-column neighborhood (SampleMargin)
/// and are lowered by HeightBias, so each level's mesh lies strictly inside the real
/// terrain AND strictly below the next finer LOD level. Wherever finer geometry is
/// rendered the depth buffer buries the coarser mesh completely; no masking or
/// camera-dependent remeshing is needed. Regions whose footprint is fully covered by
/// loaded finer regions are skipped entirely (<see cref="IsBuried"/>) so only one level
/// is drawn per area — the stacked shells are invisible from outside the terrain but
/// show through once the camera goes underground.
/// </summary>
public class LodRegion : IDisposable
{
    // Level 0 is full-detail chunks (radius 16 chunks = 256 blocks); these rings extend past it.
    // Nesting invariants: SampleMargin(L+1) >= SampleMargin(L) + CellSize(L) - 1
    //                     HeightBias(L+1)   >= HeightBias(L) + CellSize(L)
    public static readonly LodLevelSpec[] Levels =
    [
        new(CellSize: 2, RegionBlocks: 64, RegionRadius: 8, SampleMargin: 1, HeightBias: 0),   // to  512 blocks
        new(CellSize: 4, RegionBlocks: 128, RegionRadius: 8, SampleMargin: 3, HeightBias: 2),  // to 1024 blocks
        new(CellSize: 8, RegionBlocks: 256, RegionRadius: 8, SampleMargin: 7, HeightBias: 6),  // to 2048 blocks
    ];

    public const float MAX_LIFETIME = 0.5f;
    public double Lifetime;

    /// <summary>
    /// Set by <see cref="LodManager.Update"/>: every finer-level region overlapping this
    /// one is loaded, so this mesh contributes nothing and is skipped at render time.
    /// </summary>
    public bool IsBuried;

    public readonly int Level;
    public readonly int X, Z;          // region grid coordinates (in this level's grid)
    public readonly int SizeBlocks;
    public readonly float YMin, YMax;

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
        Level = data.Level;
        X = data.X;
        Z = data.Z;
        SizeBlocks = Levels[data.Level].RegionBlocks;
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
    /// Builds a region mesh on a background thread (CPU only, no GL calls).
    /// Vertices are local in X/Z (relative to the region origin) and absolute in Y.
    /// </summary>
    public static LodMeshData BuildMeshData(int level, int rx, int rz)
    {
        var spec = Levels[level];
        var cellSize = spec.CellSize;
        var cellShift = System.Numerics.BitOperations.TrailingZeroCount(cellSize);
        var cells = spec.Cells;
        var margin = spec.SampleMargin;
        var heightGrid = cells + 2; // cells plus 1-cell border for side faces

        // Prefetch the heightmaps of the chunk columns this region samples from.
        // Sampling reaches at most cellSize + margin (< Chunk.Size) past the region edge,
        // so a 1-chunk border suffices.
        var regionChunkSpan = spec.RegionBlocks / Chunk.Size;
        var chunkSpan = regionChunkSpan + 2;
        var baseChunkX = rx * regionChunkSpan - 1;
        var baseChunkZ = rz * regionChunkSpan - 1;
        var heightmaps = new int[chunkSpan * chunkSpan][];
        for (var cx = 0; cx < chunkSpan; cx++)
        for (var cz = 0; cz < chunkSpan; cz++)
            heightmaps[cx * chunkSpan + cz] = Chunk.GetColumnHeights(baseChunkX + cx, baseChunkZ + cz);

        var baseFineX = rx * spec.RegionBlocks;
        var baseFineZ = rz * spec.RegionBlocks;
        var hasCaps = !ColumnHeightCaps.IsEmpty;

        int FineHeight(int fineX, int fineZ)
        {
            var cx = (fineX >> Chunk.SizeShift) - baseChunkX;
            var cz = (fineZ >> Chunk.SizeShift) - baseChunkZ;
            var height = heightmaps[cx * chunkSpan + cz][((fineX & Chunk.SizeMask) << Chunk.SizeShift) | (fineZ & Chunk.SizeMask)];

            if (hasCaps && ColumnHeightCaps.TryGetValue((fineX, fineZ), out var cap) && cap < height)
                height = cap;

            return height;
        }

        // Top solid cell per LOD column (with 1-cell border), from the eroded heights
        var topCell = new int[heightGrid * heightGrid];
        for (var i = -1; i <= cells; i++)
        {
            for (var j = -1; j <= cells; j++)
            {
                var minHeight = int.MaxValue;
                for (var dx = -margin; dx < cellSize + margin; dx++)
                for (var dz = -margin; dz < cellSize + margin; dz++)
                {
                    var h = FineHeight(baseFineX + i * cellSize + dx, baseFineZ + j * cellSize + dz);
                    if (h < minHeight) minHeight = h;
                }

                // Cell cy is solid iff cellSize * (cy + 1) <= eroded height
                topCell[(i + 1) * heightGrid + (j + 1)] = ((minHeight - spec.HeightBias) >> cellShift) - 1;
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

        void AddQuad(int faceIndex, Vector3 cellOrigin, BlockType.Point tile, Span<byte> shades)
        {
            // Meshes are indexed with ushort; a region can't realistically exceed this,
            // but degrade gracefully instead of overflowing if terrain is extreme
            if (vertices.Count > ushort.MaxValue - 3)
                return;

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
                var pos = cellOrigin + face[c] * cellSize;
                if (pos.Y < yMin) yMin = pos.Y;
                if (pos.Y > yMax) yMax = pos.Y;
                var shade = shades[c];
                vertices.Add(new Vertex(pos, shade, shade, shade, 255, uv[c]));
            }

            indices.Add((ushort)(indexOffset + 0));
            indices.Add((ushort)(indexOffset + 1));
            indices.Add((ushort)(indexOffset + 2));
            indices.Add((ushort)(indexOffset + 0));
            indices.Add((ushort)(indexOffset + 2));
            indices.Add((ushort)(indexOffset + 3));
        }

        // Per-vertex ambient occlusion derived from the column-top grid (same formula and
        // shade table as chunk meshing): a neighboring column rising above a face corner
        // occludes it. Cell-scale AO, so the soft shadows match the LOD resolution.
        Span<byte> shades = stackalloc byte[4];

        for (var i = 0; i < cells; i++)
        {
            for (var j = 0; j < cells; j++)
            {
                var gi = i + 1;
                var gj = j + 1;
                var top = topCell[gi * heightGrid + gj];

                var topCorners = Chunk.CubeFaces[topFace].face;
                for (var c = 0; c < 4; c++)
                {
                    var dx = topCorners[c].X == 0 ? -1 : 1;
                    var dz = topCorners[c].Z == 0 ? -1 : 1;
                    var side1 = topCell[(gi + dx) * heightGrid + gj] > top;
                    var side2 = topCell[gi * heightGrid + (gj + dz)] > top;
                    var diagonal = topCell[(gi + dx) * heightGrid + (gj + dz)] > top;
                    shades[c] = Chunk.ShadeLut[(topFace << 2) | Chunk.VertexAO(side1, side2, diagonal)];
                }

                AddQuad(topFace, new Vector3(i * cellSize, top * cellSize, j * cellSize), grass.FaceTiles[topFace], shades);

                foreach (var f in sideFaces)
                {
                    var normal = Chunk.CubeFaces[f].normal;
                    var ni = gi + (int)normal.X;
                    var nj = gj + (int)normal.Z;
                    var neighborTop = topCell[ni * heightGrid + nj];
                    var faceCorners = Chunk.CubeFaces[f].face;

                    for (var cy = neighborTop + 1; cy <= top; cy++)
                    {
                        for (var c = 0; c < 4; c++)
                        {
                            var corner = faceCorners[c];
                            var v = corner.Y == 0 ? -1 : 1;

                            // Column diagonally in front of the face, toward this corner's tangent side
                            var lateralTop = normal.X != 0
                                ? topCell[ni * heightGrid + (nj + (corner.Z == 0 ? -1 : 1))]
                                : topCell[(ni + (corner.X == 0 ? -1 : 1)) * heightGrid + nj];

                            var side1 = lateralTop >= cy;
                            var side2 = neighborTop >= cy + v;
                            var diagonal = lateralTop >= cy + v;
                            shades[c] = Chunk.ShadeLut[(f << 2) | Chunk.VertexAO(side1, side2, diagonal)];
                        }

                        var tile = cy == top ? grass.FaceTiles[f] : dirt.FaceTiles[f];
                        AddQuad(f, new Vector3(i * cellSize, cy * cellSize, j * cellSize), tile, shades);
                    }
                }
            }
        }

        return new LodMeshData(level, rx, rz, vertices, indices, yMin, yMax);
    }
}

public record LodMeshData(int Level, int X, int Z, List<Vertex> Vertices, List<ushort> Indices, float YMin, float YMax);
