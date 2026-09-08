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
/// show through once the camera goes underground. For the same reason the finest level is
/// skipped wherever the full-detail chunks are drawn: caves reach into the shell band under
/// the surface, so the shell would otherwise appear inside them.
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

    /// <summary>
    /// This region's index in <see cref="LodManager.RegionList"/>, or -1 while it is not in
    /// that list. Maintained by <see cref="LodManager"/> on the main thread only.
    /// </summary>
    public int ListIndex = -1;

    public readonly int Level;
    public readonly int X, Z;          // region grid coordinates (in this level's grid)
    public readonly int SizeBlocks;
    public readonly float YMin, YMax;

    /// <summary>
    /// Per chunk column of the region (index ci * span + cj, span = SizeBlocks / Chunk.Size):
    /// the inclusive chunk-Y range that must be loaded and drawn for this shell to be hidden
    /// under full-detail chunks. See <see cref="LodManager"/>.
    /// </summary>
    public readonly int[] ColumnCyMin, ColumnCyMax;

    private Mesh? _mesh;

    // The sea sheet is a separate mesh so the renderer can draw it blended, after everything
    // opaque (see RenderManager.RenderWorldTranslucent)
    private Mesh? _waterMesh;

    public bool HasMesh => _mesh != null;

    /// <summary>Whether any cell of this region is below sea level, i.e. it has a water sheet.</summary>
    public bool HasWaterMesh => _waterMesh != null;

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
        ColumnCyMin = data.ColumnCyMin;
        ColumnCyMax = data.ColumnCyMax;
        _mesh = data.Vertices.Count == 0
            ? null
            : new Mesh(gl, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data.Vertices),
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data.Indices));
        _waterMesh = data.WaterVertices.Count == 0
            ? null
            : new Mesh(gl, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data.WaterVertices),
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data.WaterIndices));
    }

    public void Render()
    {
        _mesh?.Render();
    }

    /// <summary>Draws only this region's sea sheet; called from the blended pass.</summary>
    public void RenderWater()
    {
        _waterMesh?.Render();
    }

    public void Dispose()
    {
        _mesh?.Dispose();
        _mesh = null;
        _waterMesh?.Dispose();
        _waterMesh = null;
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

        // Top solid cell per LOD column (with 1-cell border): the eroded terrain below, the
        // canopy above it, and their max — the shell actually drawn. Canopy is sampled over
        // each cell's own footprint only (no erosion margin), so a tree lifts just its own
        // column into a leaf-textured bump instead of a plateau.
        var groundTop = new int[heightGrid * heightGrid];
        var topCell = new int[heightGrid * heightGrid];
        // Which cells the sea covers. Measured as the max over the cell's OWN footprint, with
        // no erosion margin: the eroded minimum would paint water across every beach whose
        // neighbourhood dips below sea level.
        var isSea = new bool[heightGrid * heightGrid];
        // Beaches and gravel caps read from the same uneroded max for the same reason, the
        // gravel with one noise eval at the cell centre per cell.
        var beachTop = new bool[heightGrid * heightGrid];
        var gravelTop = new bool[heightGrid * heightGrid];
        for (var i = -1; i <= cells; i++)
        {
            for (var j = -1; j <= cells; j++)
            {
                var minHeight = int.MaxValue;
                var maxOwn = int.MinValue;
                for (var dx = -margin; dx < cellSize + margin; dx++)
                for (var dz = -margin; dz < cellSize + margin; dz++)
                {
                    var h = FineHeight(baseFineX + i * cellSize + dx, baseFineZ + j * cellSize + dz);
                    if (h < minHeight) minHeight = h;
                    if (h > maxOwn && dx >= 0 && dx < cellSize && dz >= 0 && dz < cellSize) maxOwn = h;
                }

                isSea[(i + 1) * heightGrid + (j + 1)] =
                    WorldGenerationConfig.WaterEnabled && maxOwn < WorldGenerationConfig.SeaLevel;

                beachTop[(i + 1) * heightGrid + (j + 1)] = TerrainGenerator.IsBeachHeight(maxOwn);
                gravelTop[(i + 1) * heightGrid + (j + 1)] =
                    beachTop[(i + 1) * heightGrid + (j + 1)] && TerrainGenerator.IsGravelPatch(
                        baseFineX + i * cellSize + cellSize / 2, baseFineZ + j * cellSize + cellSize / 2);

                // Cell cy is solid iff cellSize * (cy + 1) <= eroded height
                var ground = ((minHeight - spec.HeightBias) >> cellShift) - 1;
                groundTop[(i + 1) * heightGrid + (j + 1)] = ground;

                // Canopy tops are always positive where present (trees need ground above the
                // minimum height), so 0 unambiguously means open sky. It must not reach the
                // cell formula: "no canopy" computes to cell -1, which would outrank deeply
                // negative terrain and pave lowlands with phantom leaf pillars.
                var top = ground;
                var canopyMax = 0;
                for (var dx = 0; dx < cellSize; dx++)
                for (var dz = 0; dz < cellSize; dz++)
                {
                    var t = TreeGenerator.CanopyTop(baseFineX + i * cellSize + dx, baseFineZ + j * cellSize + dz);
                    if (t > canopyMax) canopyMax = t;
                }

                if (canopyMax > 0)
                    top = Math.Max(top, ((canopyMax - spec.HeightBias) >> cellShift) - 1);
                topCell[(i + 1) * heightGrid + (j + 1)] = top;
            }
        }

        // Which chunks must be drawn for a full-detail column to hide this shell: everything
        // from the lowest side face (set by the neighbouring cells' tops, hence the 1-cell ring)
        // up to the top face and the real terrain surface above it. The low end reads the
        // ground grid (canopy bumps must not lift it, or shell sides would show with no chunk
        // behind them); the high end reads the bumped grid so the canopy is covered too.
        var cellsPerChunk = Chunk.Size / cellSize;
        var columnCyMin = new int[regionChunkSpan * regionChunkSpan];
        var columnCyMax = new int[regionChunkSpan * regionChunkSpan];
        for (var ci = 0; ci < regionChunkSpan; ci++)
        {
            for (var cj = 0; cj < regionChunkSpan; cj++)
            {
                var minTop = int.MaxValue;
                var maxTop = int.MinValue;
                for (var i = ci * cellsPerChunk - 1; i <= (ci + 1) * cellsPerChunk; i++)
                for (var j = cj * cellsPerChunk - 1; j <= (cj + 1) * cellsPerChunk; j++)
                {
                    var g = groundTop[(i + 1) * heightGrid + (j + 1)];
                    if (g < minTop) minTop = g;
                    var t = topCell[(i + 1) * heightGrid + (j + 1)];
                    if (t > maxTop) maxTop = t;
                }

                var maxHeight = int.MinValue;
                foreach (var h in heightmaps[(ci + 1) * chunkSpan + (cj + 1)])
                    if (h > maxHeight) maxHeight = h;

                // A sea cell hides this shell only once the chunk holding the water surface
                // is meshed too, or the sheet would vanish before the chunk water appears
                var hasSea = false;
                for (var i = ci * cellsPerChunk; i < (ci + 1) * cellsPerChunk && !hasSea; i++)
                for (var j = cj * cellsPerChunk; j < (cj + 1) * cellsPerChunk && !hasSea; j++)
                    hasSea = isSea[(i + 1) * heightGrid + (j + 1)];

                var yLo = cellSize * (minTop + 1);
                var yHi = Math.Max(cellSize * (maxTop + 1), maxHeight + 1);
                if (hasSea) yHi = Math.Max(yHi, WorldGenerationConfig.SeaLevel + 1);
                columnCyMin[ci * regionChunkSpan + cj] = yLo >> Chunk.SizeShift;
                columnCyMax[ci * regionChunkSpan + cj] = (yHi - 1) >> Chunk.SizeShift;
            }
        }

        var grass = BlockType.ById[TerrainGenerator.SurfaceBlock]!;
        var dirt = BlockType.ById[TerrainGenerator.SoilBlock]!;
        var stone = BlockType.ById[TerrainGenerator.RockBlock]!;
        var sand = BlockType.ById[TerrainGenerator.SandBlock]!;
        var gravel = BlockType.ById[TerrainGenerator.GravelBlock]!;
        var leaves = BlockType.ById[TreeGenerator.LeavesBlock]!;

        var vertices = new List<Vertex>(8192);
        var indices = new List<ushort>(12288);
        var waterVertices = new List<Vertex>();
        var waterIndices = new List<ushort>();
        var yMin = float.MaxValue;
        var yMax = float.MinValue;

        // Horizontal side faces in Chunk.CubeFaces order: -Z, +Z, -X, +X
        Span<int> sideFaces = [0, 1, 2, 3];
        const int topFace = 4;

        void AddQuad(List<Vertex> targetVertices, List<ushort> targetIndices,
            int faceIndex, Vector3 cellOrigin, BlockType.Point tile, Span<byte> shades, bool isWater = false)
        {
            // Meshes are indexed with ushort; a region can't realistically exceed this,
            // but degrade gracefully instead of overflowing if terrain is extreme
            if (targetVertices.Count > ushort.MaxValue - 3)
                return;

            var tileUv = TextureAtlas.TileUVSize;
            var u0 = tile.X * tileUv;
            var v0 = tile.Y * tileUv;
            Span<Vector2> uv =
            [
                new Vector2(u0, v0 + tileUv),
                new Vector2(u0, v0),
                new Vector2(u0 + tileUv, v0),
                new Vector2(u0 + tileUv, v0 + tileUv)
            ];

            var face = Chunk.CubeFaces[faceIndex].face;
            var normal = Chunk.CubeFaces[faceIndex].normal;
            var indexOffset = (ushort)targetVertices.Count;
            for (var c = 0; c < 4; c++)
            {
                var pos = cellOrigin + face[c] * cellSize;
                // The region AABB is what the frustum and the burial test use, so the sea
                // sheet has to widen it too
                if (pos.Y < yMin) yMin = pos.Y;
                if (pos.Y > yMax) yMax = pos.Y;
                var shade = shades[c];
                // Far shells sit in open sky: full sun, no lamp light, real face normal.
                // ponytail: vertex alpha 254 flags water for the shader (glass/terrain stay 255);
                // fColor.a is otherwise unused by the world shader, so the flag is invisible.
                var alpha = (byte)(isWater ? 254 : 255);
                targetVertices.Add(new Vertex(pos, shade, shade, shade, alpha, uv[c], 1f, 0f, normal));
            }

            targetIndices.Add((ushort)(indexOffset + 0));
            targetIndices.Add((ushort)(indexOffset + 1));
            targetIndices.Add((ushort)(indexOffset + 2));
            targetIndices.Add((ushort)(indexOffset + 0));
            targetIndices.Add((ushort)(indexOffset + 2));
            targetIndices.Add((ushort)(indexOffset + 3));
        }

        // The sea reads as one flat sheet at range, so it is a single +Y quad per sea cell at
        // the chunk water surface minus this level's height bias (the terrain shells are
        // lowered the same way) and a further LodWaterSink, which keeps level 0 just under the
        // real chunk water it meets at the full-detail ring instead of z-fighting it.
        var water = BlockType.ById[TerrainGenerator.SeaBlock]!;
        var waterTile = water.FaceTiles[topFace];
        var sheetY = WorldGenerationConfig.SeaLevel + FluidState.SourceHeight
                     - spec.HeightBias - WorldGenerationConfig.LodWaterSink;
        Span<byte> waterShades = [
            Chunk.ShadeLut[(topFace << 2) | 3], Chunk.ShadeLut[(topFace << 2) | 3],
            Chunk.ShadeLut[(topFace << 2) | 3], Chunk.ShadeLut[(topFace << 2) | 3]
        ];

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
                var ground = groundTop[gi * heightGrid + gj];

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

                // Shoreline columns wear sand (gravel where the cell is patched) like the
                // chunks; beach-ness comes from the uneroded cell max, since the eroded ground
                // top can sit a whole band below the real shoreline
                var beach = beachTop[gi * heightGrid + gj];
                var cap = beach && gravelTop[gi * heightGrid + gj] ? gravel : sand;
                var soil = beach ? sand : dirt;

                AddQuad(vertices, indices, topFace, new Vector3(i * cellSize, top * cellSize, j * cellSize),
                    top > ground ? leaves.FaceTiles[topFace]
                    : isSea[gi * heightGrid + gj] ? soil.FaceTiles[topFace]   // sea floors are dirt, sandy ones sand
                    : beach ? cap.FaceTiles[topFace]
                    : grass.FaceTiles[topFace], shades);

                // AddQuad places a +Y face one cell above its origin, so the origin sits a
                // cell below the sheet
                if (isSea[gi * heightGrid + gj])
                    AddQuad(waterVertices, waterIndices, topFace,
                        new Vector3(i * cellSize, sheetY - cellSize, j * cellSize), waterTile, waterShades, true);

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

                        // Canopy airspace wears leaves; at and below the eroded surface the
                        // grass/dirt/stone rule applies, measured from the ground (not the bump)
                        var tile = cy > ground ? leaves.FaceTiles[f]
                            : cy == top ? (isSea[gi * heightGrid + gj] ? soil : beach ? cap : grass).FaceTiles[f]
                            : (ground - cy) * cellSize <= WorldGenerationConfig.NominalDirtDepth ? soil.FaceTiles[f]
                            : stone.FaceTiles[f];
                        AddQuad(vertices, indices, f, new Vector3(i * cellSize, cy * cellSize, j * cellSize), tile, shades);
                    }
                }
            }
        }

        return new LodMeshData(level, rx, rz, vertices, indices, waterVertices, waterIndices,
            yMin, yMax, columnCyMin, columnCyMax);
    }
}

public record LodMeshData(int Level, int X, int Z, List<Vertex> Vertices, List<ushort> Indices,
    List<Vertex> WaterVertices, List<ushort> WaterIndices, float YMin, float YMax,
    int[] ColumnCyMin, int[] ColumnCyMax);
