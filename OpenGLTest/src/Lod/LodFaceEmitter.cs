using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Data;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Rendering.Geometry;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Lod;

/// <summary>
/// The face and water emission of one far-LOD region build (<see cref="LodMeshBuilder"/>): the block palette, the
/// biome tint lattice its faces sample, the four output lists (rented from the build's MeshBufferPool and never
/// returned here) and every terrain face and sea-sheet quad. Stack-only, one per build.
/// </summary>
internal ref struct LodFaceEmitter
{
    private const int TopFace = 4;

    private readonly BlockType _grass;
    private readonly BlockType _dirt;
    private readonly BlockType _stone;
    private readonly BlockType _sand;
    private readonly BlockType _gravel;
    private readonly BlockType _sandstone;
    private readonly BlockType _fallbackLeaves;
    private readonly BlockType _snow;
    private readonly BlockType _ice;
    private readonly BlockType _permafrost;
    private readonly LodLevelSpec _spec;
    private readonly int _cellSize;
    private readonly int _cells;
    private readonly int _heightGrid;
    private readonly int _regionChunkSpan;
    private readonly int _latticeSpan;
    private readonly Vector3[] _grassTint;
    private readonly Vector3[] _foliageTint;
    private readonly bool _hasRegionOverrides;
    private readonly int[] _topCell;
    private readonly int[] _groundTop;
    private readonly bool[] _beachTop;
    private readonly bool[] _gravelTop;
    private readonly bool[] _rockTop;
    private readonly bool[] _soilStripped;
    private readonly bool[] _snowTop;
    private readonly bool[] _iceTop;
    private readonly Biome[] _biomeGrid;
    private readonly int[] _waterLevels;
    private readonly byte[] _seaFloor;
    private readonly ushort[] _canopyLeaf;
    private readonly ushort[] _topBlockGrid;
    private readonly List<Vertex> _vertices;
    private readonly List<ushort> _indices;
    private readonly List<Vertex> _waterVertices;
    private readonly List<ushort> _waterIndices;
    private float _yMin;
    private float _yMax;

    internal LodFaceEmitter(MeshBufferPool? buffers, LodLevelSpec spec, int cellSize, int cells, int heightGrid,
        int regionChunkSpan, int chunkSpan, int latticeSpan, int regionChunkX, int regionChunkZ, bool[] supplied,
        ColumnData[] columns, Vector3[] grassTint, Vector3[] foliageTint, bool hasRegionOverrides, int[] topCell,
        int[] groundTop, bool[] beachTop, bool[] gravelTop, bool[] rockTop, bool[] soilStripped, bool[] snowTop,
        bool[] iceTop,
        Biome[] biomeGrid, int[] waterLevels, byte[] seaFloor, ushort[] canopyLeaf, ushort[] topBlockGrid)
    {
        _spec = spec;
        _cellSize = cellSize;
        _cells = cells;
        _heightGrid = heightGrid;
        _regionChunkSpan = regionChunkSpan;
        _latticeSpan = latticeSpan;
        _grassTint = grassTint;
        _foliageTint = foliageTint;
        _hasRegionOverrides = hasRegionOverrides;
        _topCell = topCell;
        _groundTop = groundTop;
        _beachTop = beachTop;
        _gravelTop = gravelTop;
        _rockTop = rockTop;
        _soilStripped = soilStripped;
        _snowTop = snowTop;
        _iceTop = iceTop;
        _biomeGrid = biomeGrid;
        _waterLevels = waterLevels;
        _seaFloor = seaFloor;
        _canopyLeaf = canopyLeaf;
        _topBlockGrid = topBlockGrid;

        _grass = BlockType.ById[TerrainGenerator.SurfaceBlock]!;
        _dirt = BlockType.ById[TerrainGenerator.SoilBlock]!;
        _stone = BlockType.ById[TerrainGenerator.RockBlock]!;
        _sand = BlockType.ById[TerrainGenerator.SandBlock]!;
        _gravel = BlockType.ById[TerrainGenerator.GravelBlock]!;
        _sandstone = BlockType.ById[TerrainGenerator.SandstoneBlock]!;
        _fallbackLeaves = BlockType.ById[TreeGenerator.LeavesBlock]!;
        _snow = BlockType.ById[TerrainGenerator.SnowBlock]!;
        _ice = BlockType.ById[TerrainGenerator.IceBlock]!;
        _permafrost = BlockType.ById[TerrainGenerator.PermafrostBlock]!;

        // Fill the tint lattice: one climate evaluation per point gives both palettes. Lattice
        // point (li, lj) is the minimum corner of chunk column (regionChunkX + li, regionChunkZ + lj),
        // prefetched at (li + 1, lj + 1); a column the source supplies stores the climate at exactly
        // that corner (ColumnData.CornerWarmth), so its tint reads the stored value, as the chunk
        // mesher's field does.
        for (var li = 0; li < latticeSpan; li++)
        for (var lj = 0; lj < latticeSpan; lj++)
        {
            var k = (li + 1) * chunkSpan + (lj + 1);
            if (supplied[k])
                BiomeTint.TintsFromClimate(columns[k].CornerWarmth, columns[k].CornerWetness,
                    out grassTint[li * latticeSpan + lj], out foliageTint[li * latticeSpan + lj]);
            else
                BiomeTint.LatticeTints(regionChunkX + li, regionChunkZ + lj,
                    out grassTint[li * latticeSpan + lj], out foliageTint[li * latticeSpan + lj]);
        }

        // Rented, not allocated: LodManager returns them on the main thread once the region is on
        // the GPU, or when a finished build is dropped out of range — never here, on the LodGen
        // worker, because a completed LodMeshData can wait in the upload queue across frames.
        _vertices = buffers?.RentVertices() ?? new List<Vertex>(LodRegion.MeshVertexCapacity);
        _indices = buffers?.RentIndices() ?? new List<ushort>(LodRegion.MeshIndexCapacity);
        _waterVertices = buffers?.RentTranslucentVertices() ?? new List<Vertex>(LodRegion.WaterVertexCapacity);
        _waterIndices = buffers?.RentTranslucentIndices() ?? new List<ushort>(LodRegion.WaterIndexCapacity);
        _yMin = float.MaxValue;
        _yMax = float.MinValue;
    }

    private BlockType CanopyBlock(ushort leafId) =>
        (leafId != 0 ? BlockType.ById[leafId] : null) ?? _fallbackLeaves;

    // Ocean floor block by the depth band the chunk mesher's GenerateColumn would pick for the
    // same height (0 sand, 1 gravel, 2 stone), so far seabed matches near seabed. A land biome's
    // lake or river bed keeps the soil the column wears under water instead (3), which is dirt,
    // and the frozen biome's bed is its permafrost (4).
    private BlockType FloorBlock(byte material) => material switch
    {
        0 => _sand,
        1 => _gravel,
        3 => _dirt,
        4 => _permafrost,
        _ => _stone,
    };

    // The tint at a region-local position, interpolated over the lattice cell of the chunk
    // column it stands in exactly as the chunk mesher's BiomeTintField.Sample does. The far edge
    // (x == RegionBlocks) reads the last column at fx = 1, which the weighted-sum Bilinear makes
    // bit-identical to the next region's first column at fx = 0.
    private Vector3 SampleTint(BiomeTintPalette palette, Vector3 regionLocalPos)
    {
        var grid = palette == BiomeTintPalette.Foliage ? _foliageTint : _grassTint;
        var cx = Math.Clamp((int)regionLocalPos.X >> Chunk.SizeShift, 0, _regionChunkSpan - 1);
        var cz = Math.Clamp((int)regionLocalPos.Z >> Chunk.SizeShift, 0, _regionChunkSpan - 1);
        var fx = regionLocalPos.X / Chunk.Size - cx;
        var fz = regionLocalPos.Z / Chunk.Size - cz;
        return BiomeTint.Bilinear(
            grid[cx * _latticeSpan + cz], grid[(cx + 1) * _latticeSpan + cz],
            grid[cx * _latticeSpan + cz + 1], grid[(cx + 1) * _latticeSpan + cz + 1], fx, fz);
    }

    private void AddQuad(List<Vertex> targetVertices, List<ushort> targetIndices,
        int faceIndex, Vector3 cellOrigin, ushort tile, scoped Span<byte> shades, scoped ReadOnlySpan<float> suns,
        bool isWater = false, bool lodLeaves = false, BiomeTintPalette? tinted = null)
    {
        // Meshes are indexed with ushort; a region can't realistically exceed this,
        // but degrade gracefully instead of overflowing if terrain is extreme
        if (targetVertices.Count > ushort.MaxValue - 3)
            return;

        // One whole tile per LOD cell: tile-local UVs on the tile's atlas slice
        Span<Vector2> uv =
        [
            new Vector2(0f, 1f),
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(1f, 1f)
        ];

        var face = Chunk.CubeFaces[faceIndex].face;
        var normal = Chunk.CubeFaces[faceIndex].normal;
        var indexOffset = (ushort)targetVertices.Count;
        for (var c = 0; c < 4; c++)
        {
            var pos = cellOrigin + face[c] * _cellSize;
            // The region AABB is what the frustum and the burial test use, so the sea
            // sheet has to widen it too
            if (pos.Y < _yMin) _yMin = pos.Y;
            if (pos.Y > _yMax) _yMax = pos.Y;
            var shade = shades[c];
            // Sun is the coarse height-derived sky exposure (LodSkyExposure); no lamp
            // light, real face normal.
            // ponytail: vertex alpha flags the fragment path (254 = water, 253 = opaque
            // LOD foliage, 255 = everything else); fColor.a is otherwise unused by the
            // world shader, so the flags are invisible.
            var alpha = (byte)(isWater ? 254 : lodLeaves ? 253 : 255);
            if (tinted is { } palette)
            {
                // A biome-tinted face bakes shade * tint into the rgb like the chunk mesher
                // (ChunkMesher.EmitTintedCubeFace); bit 4 makes the fragment apply BiomeTint.Gain.
                BiomeTint.Shade(shade, SampleTint(palette, pos), out var r, out var g, out var b);
                targetVertices.Add(new Vertex(pos, r, g, b, alpha, uv[c], tile, suns[c], 0f, normal,
                    biomeTint: true));
            }
            else
            {
                targetVertices.Add(new Vertex(pos, shade, shade, shade, alpha, uv[c], tile, suns[c], 0f, normal));
            }
        }

        targetIndices.Add((ushort)(indexOffset + 0));
        targetIndices.Add((ushort)(indexOffset + 1));
        targetIndices.Add((ushort)(indexOffset + 2));
        targetIndices.Add((ushort)(indexOffset + 0));
        targetIndices.Add((ushort)(indexOffset + 2));
        targetIndices.Add((ushort)(indexOffset + 3));
    }

    // One terrain face of a whole block. A biome-tinted block draws what its BiomeTint says, the
    // same order the chunk mesher uses: the untinted base quad first (grass sides: dirt), then
    // the tinted quad coplanar over it, so Lequal lets the overlay win; a `tile` face is the
    // tinted quad alone. Every other block wears its plain face tile.
    private void AddBlockFace(int face, Vector3 origin, BlockType block, scoped Span<byte> faceShades,
        scoped ReadOnlySpan<float> faceSuns, bool lodLeaves = false)
    {
        if (block.BiomeTint is { } bt)
        {
            if (bt.Base[face] != BiomeTintFaces.NoTile)
                AddQuad(_vertices, _indices, face, origin, bt.Base[face], faceShades, faceSuns, lodLeaves: lodLeaves);
            if (bt.Tinted[face] != BiomeTintFaces.NoTile)
                AddQuad(_vertices, _indices, face, origin, bt.Tinted[face], faceShades, faceSuns,
                    lodLeaves: lodLeaves, tinted: bt.Palette);
            return;
        }

        AddQuad(_vertices, _indices, face, origin, block.FaceTiles[face], faceShades, faceSuns, lodLeaves: lodLeaves);
    }

    internal void Emit()
    {
        // Horizontal side faces in Chunk.CubeFaces order: -Z, +Z, -X, +X
        Span<int> sideFaces = [0, 1, 2, 3];

        // The water reads as one flat sheet at range, so it is a single +Y quad per wet cell at that
        // cell's own level minus this level's height bias (the terrain shells are lowered the same
        // way) and a further LodWaterSink, which keeps level 0 just under the real chunk water it
        // meets at the full-detail ring instead of z-fighting it. A lake carries its own level, so
        // each cell adds its surface rather than one sea plane.
        var water = BlockType.ById[TerrainGenerator.SeaBlock]!;
        var waterTile = water.FaceTiles[TopFace];
        var sheetDrop = FluidState.SourceHeight - _spec.HeightBias - WaterRenderConfig.LodWaterSink;
        Span<byte> waterShades = [
            ChunkFaceTables.ShadeLut[(TopFace << 2) | 3], ChunkFaceTables.ShadeLut[(TopFace << 2) | 3],
            ChunkFaceTables.ShadeLut[(TopFace << 2) | 3], ChunkFaceTables.ShadeLut[(TopFace << 2) | 3]
        ];

        // Per-vertex ambient occlusion derived from the column-top grid (same formula and
        // shade table as chunk meshing): a neighboring column rising above a face corner
        // occludes it. Cell-scale AO, so the soft shadows match the LOD resolution.
        Span<byte> shades = stackalloc byte[4];
        // Per-vertex sky exposure from the same grid (LodSkyExposure); the sea/ice sheet is open sky
        Span<float> suns = stackalloc float[4];
        Span<float> waterSuns = [1f, 1f, 1f, 1f];

        for (var i = 0; i < _cells; i++)
        {
            for (var j = 0; j < _cells; j++)
                EmitCell(i, j, sideFaces, shades, suns, waterTile, sheetDrop, waterShades, waterSuns);
        }
    }

    private void EmitCell(int i, int j, scoped Span<int> sideFaces, scoped Span<byte> shades, scoped Span<float> suns,
        ushort waterTile, float sheetDrop, scoped Span<byte> waterShades, scoped Span<float> waterSuns)
    {
        var gi = i + 1;
        var gj = j + 1;
        var top = _topCell[gi * _heightGrid + gj];
        var ground = _groundTop[gi * _heightGrid + gj];

        var topCorners = Chunk.CubeFaces[TopFace].face;
        for (var c = 0; c < 4; c++)
        {
            var dx = topCorners[c].X == 0 ? -1 : 1;
            var dz = topCorners[c].Z == 0 ? -1 : 1;
            var side1 = _topCell[(gi + dx) * _heightGrid + gj] > top;
            var side2 = _topCell[gi * _heightGrid + (gj + dz)] > top;
            var diagonal = _topCell[(gi + dx) * _heightGrid + (gj + dz)] > top;
            shades[c] = ChunkFaceTables.ShadeLut[(TopFace << 2) | ChunkFaceTables.VertexAO(side1, side2, diagonal)];
            suns[c] = LodSkyExposure.TopFace(ground, top,
                _topCell[(gi + dx) * _heightGrid + gj],
                _topCell[gi * _heightGrid + (gj + dz)],
                _topCell[(gi + dx) * _heightGrid + (gj + dz)]);
        }

        // Shoreline columns wear sand (gravel where the cell is patched) like the
        // chunks; beach-ness comes from the uneroded cell max, since the eroded ground
        // top can sit a whole band below the real shoreline
        var beach = _beachTop[gi * _heightGrid + gj];
        var cap = beach && _gravelTop[gi * _heightGrid + gj] ? _gravel : _sand;
        var soil = beach ? _sand : _dirt;
        var rock = _rockTop[gi * _heightGrid + gj];
        var stripped = _soilStripped[gi * _heightGrid + gj];
        var snowCell = _snowTop[gi * _heightGrid + gj];
        var biome = _biomeGrid[gi * _heightGrid + gj];
        var cellLevel = _waterLevels[gi * _heightGrid + gj];
        var isSeaCell = cellLevel > TerrainGenerator.NoWater;
        var sheetY = isSeaCell ? cellLevel + sheetDrop : 0f;
        var floor = _seaFloor[gi * _heightGrid + gj];
        // The edited surface's own block (see the tile rule), which outranks the generated
        // material chain wherever it is the drawn surface.
        var edited = _hasRegionOverrides && _topBlockGrid[gi * _heightGrid + gj] is var eb and not 0
            ? BlockType.ById[eb]
            : null;

        // The block the cell's drawn surface wears: the canopy bump's leaves above the
        // ground, else the edited block, else the generated material chain.
        var surface = edited
            ?? (isSeaCell ? FloorBlock(floor)   // seabed wears its depth band, not dirt
            : beach ? cap
            : rock ? _stone
            : snowCell ? _snow
            : biome == Biome.Desert ? _sand
            : _grass);
        AddBlockFace(TopFace, new Vector3(i * _cellSize, top * _cellSize, j * _cellSize),
            top > ground ? CanopyBlock(_canopyLeaf[gi * _heightGrid + gj]) : surface,
            shades, suns, lodLeaves: top > ground);

        // AddQuad places a +Y face one cell above its origin, so the origin sits a
        // cell below the sheet. A frozen cell shows snow-covered ice instead of open
        // water, and is not flagged water: the flag selects the fragment-shader water
        // model regardless of tile, so flagging ice would shade it as water.
        if (isSeaCell)
        {
            var frozen = _iceTop[gi * _heightGrid + gj];
            var snowCovered = _snowTop[gi * _heightGrid + gj];
            AddQuad(_waterVertices, _waterIndices, TopFace,
                new Vector3(i * _cellSize, sheetY - _cellSize, j * _cellSize),
                frozen ? (snowCovered ? _snow.FaceTiles[TopFace] : _ice.FaceTiles[TopFace]) : waterTile,
                waterShades, waterSuns, isWater: !frozen);
        }

        foreach (var f in sideFaces)
        {
            var normal = Chunk.CubeFaces[f].normal;
            var ni = gi + (int)normal.X;
            var nj = gj + (int)normal.Z;
            var neighborTop = _topCell[ni * _heightGrid + nj];
            var faceCorners = Chunk.CubeFaces[f].face;

            for (var cy = neighborTop + 1; cy <= top; cy++)
            {
                for (var c = 0; c < 4; c++)
                {
                    var corner = faceCorners[c];
                    var v = corner.Y == 0 ? -1 : 1;

                    // Column diagonally in front of the face, toward this corner's tangent side
                    var lateralTop = normal.X != 0
                        ? _topCell[ni * _heightGrid + (nj + (corner.Z == 0 ? -1 : 1))]
                        : _topCell[(ni + (corner.X == 0 ? -1 : 1)) * _heightGrid + nj];

                    var side1 = lateralTop >= cy;
                    var side2 = neighborTop >= cy + v;
                    var diagonal = lateralTop >= cy + v;
                    shades[c] = ChunkFaceTables.ShadeLut[(f << 2) | ChunkFaceTables.VertexAO(side1, side2, diagonal)];

                    // Sky exposure reads only columns in front of the face: both front
                    // diagonals and the one straight ahead, never the coplanar wall column
                    var oppositeDiagonal = normal.X != 0
                        ? _topCell[ni * _heightGrid + (nj + (corner.Z == 0 ? 1 : -1))]
                        : _topCell[(ni + (corner.X == 0 ? 1 : -1)) * _heightGrid + nj];
                    var vertexY = cy + (corner.Y == 0 ? 0 : 1);
                    suns[c] = LodSkyExposure.SideFace(vertexY, cy, ground, top,
                        lateralTop, oppositeDiagonal, neighborTop);
                }

                // Canopy airspace wears leaves; at and below the eroded surface the
                // grass/dirt/stone rule applies, measured from the ground (not the bump),
                // with the biome's own top material and soil band beneath it
                var sideOrigin = new Vector3(i * _cellSize, cy * _cellSize, j * _cellSize);
                if (cy > ground)
                {
                    AddBlockFace(f, sideOrigin, CanopyBlock(_canopyLeaf[gi * _heightGrid + gj]),
                        shades, suns, lodLeaves: true);
                }
                else if (cy == top)
                {
                    AddBlockFace(f, sideOrigin, surface, shades, suns);
                }
                else
                {
                    // The soil/stone band below the top cell never tints. The frozen biome's band is
                    // its permafrost, or stone where the column's soil was stripped.
                    var tile = (ground - cy) * _cellSize <= WorldGenerationConfig.NominalDirtDepth
                        ? (biome == Biome.Desert ? _sandstone.FaceTiles[f]
                            : isSeaCell || rock ? _stone.FaceTiles[f]
                            : biome == Biome.Frozen ? (stripped ? _stone : _permafrost).FaceTiles[f]
                            : soil.FaceTiles[f])
                        : _stone.FaceTiles[f];
                    AddQuad(_vertices, _indices, f, sideOrigin, tile, shades, suns);
                }
            }
        }
    }

    internal LodMeshData ToMeshData(int level, int rx, int rz, int[] columnCyMin, int[] columnCyMax) =>
        new LodMeshData(level, rx, rz, _vertices, _indices, _waterVertices, _waterIndices,
            _yMin, _yMax, columnCyMin, columnCyMax);
}
