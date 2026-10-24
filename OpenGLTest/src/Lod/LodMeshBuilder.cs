using System;
using System.Buffers;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.Rendering.Geometry;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;

namespace VoxelEngine.Lod;

/// <summary>
/// The body of <see cref="LodRegion.BuildMeshData"/>: one far-LOD region build, CPU only, no GL calls.
/// One instance per build, on the calling thread (a LodGen worker, the server's main thread or a test); its grids are
/// ArrayPool rentals made by the constructor and handed back by ReturnRentals in Build's finally.
/// </summary>
internal sealed class LodMeshBuilder
{
    private readonly bool _separableMin;
    private readonly LodLevelSpec _spec;
    private readonly int _cellSize;
    private readonly int _cellShift;
    private readonly int _cells;
    private readonly int _margin;
    private readonly int _heightGrid;
    private readonly int _regionChunkSpan;
    private readonly int _chunkSpan;
    private readonly int _baseChunkX;
    private readonly int _baseChunkZ;
    private readonly int[][] _heightmaps;
    private readonly ColumnData[] _columns;
    private readonly bool[] _supplied;
    private bool _anySupplied;
    private readonly int[][]? _capmaps;
    private readonly int[][]? _overrideMaps;
    private bool _hasRegionOverrides;
    private readonly int _baseFineX;
    private readonly int _baseFineZ;
    private readonly int _fineMargin;
    private readonly int _fineSpan;
    private readonly int _fineOriginX;
    private readonly int _fineOriginZ;
    private readonly int _windowSpan;
    private readonly int[] _groundTop;
    private readonly int[] _topCell;
    private readonly int[] _waterLevels;
    private readonly bool[] _beachTop;
    private readonly bool[] _gravelTop;
    private readonly bool[] _snowTop;
    private readonly bool[] _iceTop;
    private readonly bool[] _rockTop;
    private readonly Biome[] _biomeGrid;
    private readonly byte[] _seaFloor;
    private readonly byte[] _canopyLeaf;
    private readonly int[] _fine;
    private readonly int[] _xMin;
    private readonly int[] _fineGen;
    private readonly byte[] _fineBlock;
    private readonly byte[] _topBlockGrid;
    private readonly int _regionChunkX;
    private readonly int _regionChunkZ;
    private readonly int _latticeSpan;
    private readonly Vector3[] _grassTint;
    private readonly Vector3[] _foliageTint;

    internal static LodMeshData Build(int level, int rx, int rz, MeshBufferPool? buffers, bool separableMin,
        IWorldSource? source)
    {
        var build = new LodMeshBuilder(level, rx, rz, separableMin, source);
        try
        {
            if (separableMin) build.FillFineGrid();
            build.FillCellGrids();
            build.FillBurialRanges(out var columnCyMin, out var columnCyMax);
            return build.EmitMesh(level, rx, rz, buffers, columnCyMin, columnCyMax);
        }
        finally
        {
            build.ReturnRentals();
        }
    }

    private LodMeshBuilder(int level, int rx, int rz, bool separableMin, IWorldSource? source)
    {
        _separableMin = separableMin;
        source ??= GeneratedWorldSource.Instance;
        _spec = LodRegion.Levels[level];
        _cellSize = _spec.CellSize;
        _cellShift = System.Numerics.BitOperations.TrailingZeroCount(_cellSize);
        _cells = _spec.Cells;
        _margin = _spec.SampleMargin;
        _heightGrid = _cells + 2; // cells plus 1-cell border for side faces

        // Prefetch the heightmaps of the chunk columns this region samples from.
        // Sampling reaches at most cellSize + margin (< Chunk.Size) past the region edge,
        // so a 1-chunk border suffices.
        _regionChunkSpan = _spec.RegionBlocks / Chunk.Size;
        _chunkSpan = _regionChunkSpan + 2;
        _baseChunkX = rx * _regionChunkSpan - 1;
        _baseChunkZ = rz * _regionChunkSpan - 1;
        var hasCaps = !LodColumnCaps.IsEmpty;
        var hasOverrides = !LodSurface.IsEmpty;
        _heightmaps = new int[_chunkSpan * _chunkSpan][];
        // The source's column and whether it supplies it, per prefetched chunk column (same shape
        // as heightmaps). A supplied column's cell decisions read its ColumnData; anySupplied keeps
        // a region the source supplies nothing of on the exact worldgen path.
        _columns = new ColumnData[_chunkSpan * _chunkSpan];
        _supplied = new bool[_chunkSpan * _chunkSpan];
        _anySupplied = false;
        // One cap-bucket resolution per prefetched heightmap (item C9). Null for every chunk column
        // the player has never edited — which is all of them until the first mined block — and the
        // whole array is skipped while the table is empty. Same shape and lifetime as heightmaps.
        _capmaps = hasCaps ? new int[_chunkSpan * _chunkSpan][] : null;
        // The LodSurface override buckets, resolved the same way and with the same shape.
        _overrideMaps = hasOverrides ? new int[_chunkSpan * _chunkSpan][] : null;
        // Whether any chunk column this region samples carries an edit. Only then does the build
        // pay for the generated-only grid and the tile rule; an unedited region keeps the old path.
        var hasEdits = false;
        _hasRegionOverrides = false;
        Prefetch(source, ref hasEdits);

        _baseFineX = rx * _spec.RegionBlocks;
        _baseFineZ = rz * _spec.RegionBlocks;

        // Fine-height grid covering the union of every cell's sample window: the region grown by
        // cellSize + margin on each side - the same reach LodManager.MarkEditedColumn uses. Filling
        // it chunk column by chunk column out of the prefetched heightmaps turns each sample into a
        // contiguous copy (the per-sample chunk-index math FineHeight does disappears) and applies
        // the C9 cap bucket and the LodSurface override bucket once per chunk column as it copies.
        _fineMargin = _cellSize + _margin;                  // 3 / 7 / 15, always < Chunk.Size
        _fineSpan = _spec.RegionBlocks + 2 * _fineMargin;   // 70 / 142 / 286
        _fineOriginX = _baseFineX - _fineMargin;
        _fineOriginZ = _baseFineZ - _fineMargin;
        _windowSpan = _cellSize + 2 * _margin;              // 4 / 10 / 22

        // Top solid cell per LOD column (with 1-cell border): the eroded terrain below, the
        // canopy above it, and their max — the shell actually drawn. Canopy is sampled over
        // each cell's own footprint only (no erosion margin), so a tree lifts just its own
        // column into a leaf-textured bump instead of a plateau. Rented: a region build would
        // otherwise allocate five grids per region on the worker.
        var gridSize = _heightGrid * _heightGrid;
        _groundTop = ArrayPool<int>.Shared.Rent(gridSize);
        _topCell = ArrayPool<int>.Shared.Rent(gridSize);
        // The level each cell's water sits at (TerrainGenerator.NoWater where it holds none), read
        // at the cell center. Wetness is measured as the max over the cell's OWN footprint, with
        // no erosion margin: the eroded minimum would paint water across every beach whose
        // neighborhood dips below the waterline. A level rather than a flag, because a lake's
        // sheet sits at the lake's own surface instead of at sea level.
        _waterLevels = ArrayPool<int>.Shared.Rent(gridSize);
        // Beaches and gravel caps read from the same uneroded max for the same reason, the
        // gravel with one noise eval at the cell center per cell.
        _beachTop = ArrayPool<bool>.Shared.Rent(gridSize);
        _gravelTop = ArrayPool<bool>.Shared.Rent(gridSize);
        // Cold cells: snow covers their land surface, and the coldest freeze the sea sheet on top
        // of the sea. Both read ColdWeight so the far snow and ice lines match the near chunks.
        _snowTop = ArrayPool<bool>.Shared.Rent(gridSize);
        _iceTop = ArrayPool<bool>.Shared.Rent(gridSize);
        // Bare-rock mountain tops, matching the chunk mesher's rock line, so a distant peak
        // shows the same stone cap the near chunks do.
        _rockTop = ArrayPool<bool>.Shared.Rent(gridSize);
        _biomeGrid = ArrayPool<Biome>.Shared.Rent(gridSize);
        _seaFloor = ArrayPool<byte>.Shared.Rent(gridSize);
        _canopyLeaf = ArrayPool<byte>.Shared.Rent(gridSize);
        // Item C1: the fine heights every cell window reads (index fx * fineSpan + fz), and one row
        // of the separable min's horizontal half, rebuilt per cell row and reused by every column.
        // Level 2 is the largest at 81796 ints, well inside ArrayPool<int>.Shared's largest bucket.
        // Both are rented unconditionally, seam path included, so the finally has no condition.
        _fine = ArrayPool<int>.Shared.Rent(_fineSpan * _fineSpan);
        _xMin = ArrayPool<int>.Shared.Rent(_fineSpan);
        // Edited regions only: the GENERATED fine heights (no override, no cap), which the per-cell
        // uneroded max reads so water, sea floor, beach and rock stay the generated decisions — a
        // pillar in the shallows must never delete the sea sheet. An unedited region aliases it to
        // `fine`, which is then the same numbers, and pays for no second grid.
        _fineGen = hasEdits ? ArrayPool<int>.Shared.Rent(_fineSpan * _fineSpan) : _fine;
        // The override block of each fine column while that override is its drawn height (0
        // otherwise), and the block each cell's drawn surface wears by the tile rule below (0 =
        // the generated tile chain). Only read while the region has overrides.
        _fineBlock = ArrayPool<byte>.Shared.Rent(_fineSpan * _fineSpan);
        _topBlockGrid = ArrayPool<byte>.Shared.Rent(gridSize);
        // The biome tint lattice (BiomeTint) at this region's chunk-column corners, both palettes
        // from one climate evaluation per point. Indexed from the region's OWN first chunk column,
        // never from baseChunkX, which is the prefetch origin one chunk before it: the chunk mesher
        // samples BiomeTint.FieldFor(cx, cz) of the chunk a vertex stands in, so the LOD has to read
        // the very same lattice points or its grass would shift a chunk against the near terrain.
        _regionChunkX = rx * _regionChunkSpan;   // = baseChunkX + 1
        _regionChunkZ = rz * _regionChunkSpan;   // = baseChunkZ + 1
        _latticeSpan = _regionChunkSpan + 1;
        _grassTint = ArrayPool<Vector3>.Shared.Rent(_latticeSpan * _latticeSpan);
        _foliageTint = ArrayPool<Vector3>.Shared.Rent(_latticeSpan * _latticeSpan);
    }

    private void Prefetch(IWorldSource source, ref bool hasEdits)
    {
        for (var cx = 0; cx < _chunkSpan; cx++)
        for (var cz = 0; cz < _chunkSpan; cz++)
        {
            var column = source.GetColumn(_baseChunkX + cx, _baseChunkZ + cz);
            _columns[cx * _chunkSpan + cz] = column;
            _heightmaps[cx * _chunkSpan + cz] = column.Heights;
            _supplied[cx * _chunkSpan + cz] = source.Supplies(_baseChunkX + cx, _baseChunkZ + cz);
            _anySupplied |= _supplied[cx * _chunkSpan + cz];
            if (_capmaps != null && LodColumnCaps.BucketOf(_baseChunkX + cx, _baseChunkZ + cz) is { } bucket)
            {
                _capmaps[cx * _chunkSpan + cz] = bucket;
                hasEdits = true;
            }

            if (_overrideMaps != null && LodSurface.BucketOf(_baseChunkX + cx, _baseChunkZ + cz) is { } overrides)
            {
                _overrideMaps[cx * _chunkSpan + cz] = overrides;
                hasEdits = true;
                _hasRegionOverrides = true;
            }
        }
    }

    // The seam path's per-sample read of the EDITED height: the override replaces the generated
    // height, then the cap lowers it, exactly the order the separable fill applies. block is
    // the override's block while that override is the column's drawn height, else 0.
    int FineHeight(int fineX, int fineZ, out byte block)
    {
        var cx = (fineX >> Chunk.SizeShift) - _baseChunkX;
        var cz = (fineZ >> Chunk.SizeShift) - _baseChunkZ;
        var local = ((fineX & Chunk.SizeMask) << Chunk.SizeShift) | (fineZ & Chunk.SizeMask);
        var height = _heightmaps[cx * _chunkSpan + cz][local];
        block = 0;

        var overrides = _overrideMaps?[cx * _chunkSpan + cz];
        var packed = overrides != null ? overrides[local] : LodSurface.None;
        if (packed != LodSurface.None) LodSurface.Unpack(packed, out height, out block);

        var caps = _capmaps?[cx * _chunkSpan + cz];
        if (caps != null && caps[local] < height)
        {
            height = caps[local];
            block = 0;
        }

        return height;
    }

    // The seam path's GENERATED height, ignoring every edit: what the water, beach and rock
    // decisions read, so a player edit never turns a sea cell into land or the reverse.
    int FineHeightGen(int fineX, int fineZ)
    {
        var cx = (fineX >> Chunk.SizeShift) - _baseChunkX;
        var cz = (fineZ >> Chunk.SizeShift) - _baseChunkZ;
        var local = ((fineX & Chunk.SizeMask) << Chunk.SizeShift) | (fineZ & Chunk.SizeMask);
        return _heightmaps[cx * _chunkSpan + cz][local];
    }

    private void FillFineGrid()
    {
        // Every grid element is written before it is read: the prefetched chunk columns span
        // fine X [baseFineX - 16, baseFineX + RegionBlocks + 16), which strictly contains the
        // grid's [baseFineX - fineMargin, baseFineX + RegionBlocks + fineMargin) because
        // fineMargin <= 15. ArrayPool hands back uncleared arrays, so that total coverage is
        // load-bearing - narrowing this fill would silently read a previous region's heights.
        for (var cx = 0; cx < _chunkSpan; cx++)
        for (var cz = 0; cz < _chunkSpan; cz++)
            FillFineColumn(cx, cz);
    }

    private void FillFineColumn(int cx, int cz)
    {
        var heights = _heightmaps[cx * _chunkSpan + cz];
        var caps = _capmaps?[cx * _chunkSpan + cz];
        var overrides = _overrideMaps?[cx * _chunkSpan + cz];
        var wx0 = (_baseChunkX + cx) << Chunk.SizeShift;
        var wz0 = (_baseChunkZ + cz) << Chunk.SizeShift;
        var lx0 = Math.Max(0, _fineOriginX - wx0);
        var lx1 = Math.Min(Chunk.Size, _fineOriginX + _fineSpan - wx0);
        var lz0 = Math.Max(0, _fineOriginZ - wz0);
        var lz1 = Math.Min(Chunk.Size, _fineOriginZ + _fineSpan - wz0);
        for (var lx = lx0; lx < lx1; lx++)
        {
            var src = lx << Chunk.SizeShift;
            var dst = (wx0 + lx - _fineOriginX) * _fineSpan + (wz0 - _fineOriginZ);
            // Split on edits so the unedited case - every chunk column in an unedited
            // world - stays a branch-free copy.
            if (caps == null && overrides == null)
            {
                for (var lz = lz0; lz < lz1; lz++)
                    _fine[dst + lz] = heights[src + lz];
                if (!ReferenceEquals(_fineGen, _fine))
                    Array.Copy(_fine, dst + lz0, _fineGen, dst + lz0, lz1 - lz0);
                // ArrayPool hands back uncleared arrays, so a clean column says so.
                if (_hasRegionOverrides)
                    Array.Clear(_fineBlock, dst + lz0, lz1 - lz0);
            }
            else
                for (var lz = lz0; lz < lz1; lz++)
                {
                    // Override first (replace), then the cap (min), as FineHeight does.
                    var h = heights[src + lz];
                    _fineGen[dst + lz] = h;
                    byte b = 0;
                    var o = overrides != null ? overrides[src + lz] : LodSurface.None;
                    if (o != LodSurface.None) LodSurface.Unpack(o, out h, out b);
                    var c = caps != null ? caps[src + lz] : LodColumnCaps.NoCap;
                    if (c < h)
                    {
                        h = c;
                        b = 0;   // a cap below the override: the override is not what shows
                    }
                    _fine[dst + lz] = h;
                    _fineBlock[dst + lz] = b;
                }
        }
    }

    private void FillCellGrids()
    {
        // Cell row i's X window starts at grid offset i * cellSize - margin + fineMargin, which is
        // exactly (i + 1) * cellSize because fineMargin == cellSize + margin: 0 at i == -1 and
        // RegionBlocks + cellSize at i == cells, whose window end + windowSpan is exactly fineSpan.
        // Nothing indexes outside the grid and no clamping is involved - the grid is sized to the
        // one-cell overrun rather than clamped against it.
        for (var i = -1; i <= _cells; i++)
        {
            if (_separableMin)
                FillRowMin(i);

            for (var j = -1; j <= _cells; j++)
                FillCell(i, j);
        }
    }

    private void FillRowMin(int i)
    {
        // Horizontal half of the separable rectangular min: one minimum per fine Z over
        // this cell row's X window, reused by every j below. Integer min is associative and
        // commutative, so min(window) == min over Z of (min over X) - bit-identical to the
        // nested scan, including the i == -1 / i == cells rows that reach one cell past the
        // region. Both accesses are stride 1 and the whole grid is L2-resident, and
        // adjacent cell rows re-read windowSpan - cellSize of the same rows.
        var wx = (i + 1) * _cellSize;
        Array.Copy(_fine, wx * _fineSpan, _xMin, 0, _fineSpan);
        for (var w = 1; w < _windowSpan; w++)
        {
            var row = _fine.AsSpan((wx + w) * _fineSpan, _fineSpan);
            var acc = _xMin.AsSpan(0, _fineSpan);
            for (var fz = 0; fz < _fineSpan; fz++)
                if (row[fz] < acc[fz]) acc[fz] = row[fz];
        }
    }

    private void FillCell(int i, int j)
    {
        int minHeight;
        var maxOwn = int.MinValue;
        if (_separableMin)
            SampleSeparable(i, j, out minHeight, ref maxOwn);
        else
            SampleSeam(i, j, out minHeight, ref maxOwn);

        // Biome read at the cell center, like the gravel patch below: the coarse cell
        // wears one material even where its footprint straddles a border.
        var centerX = _baseFineX + i * _cellSize + _cellSize / 2;
        var centerZ = _baseFineZ + j * _cellSize + _cellSize / 2;

        // The column the cell center stands in, when the source supplies it. Cells are
        // aligned to chunk columns (cellSize divides Chunk.Size), so the whole footprint
        // stands in it, and the 1-cell border stays inside the prefetched span exactly as
        // FineHeight's reads do.
        ColumnData? suppliedColumn = null;
        var centerLocal = 0;
        if (_anySupplied)
        {
            var k = ((centerX >> Chunk.SizeShift) - _baseChunkX) * _chunkSpan
                + ((centerZ >> Chunk.SizeShift) - _baseChunkZ);
            if (_supplied[k])
            {
                suppliedColumn = _columns[k];
                centerLocal = ((centerX & Chunk.SizeMask) << Chunk.SizeShift) | (centerZ & Chunk.SizeMask);
            }
        }

        // Cell cy is solid iff cellSize * (cy + 1) <= eroded height
        var ground = ((minHeight - _spec.HeightBias) >> _cellShift) - 1;
        _groundTop[(i + 1) * _heightGrid + (j + 1)] = ground;

        // Canopy tops are always positive where present (trees need ground above the
        // minimum height), so 0 unambiguously means open sky. It must not reach the
        // cell formula: "no canopy" computes to cell -1, which would outrank deeply
        // negative terrain and pave lowlands with phantom leaf pillars.
        var top = ground;
        var canopyMax = 0;
        byte canopyId = 0;

        if (suppliedColumn == null)
            ClassifyGenerated(i, j, centerX, centerZ, maxOwn, ref canopyMax, ref canopyId);
        else
            ClassifySupplied(i, j, suppliedColumn, centerLocal, maxOwn);

        if (canopyMax > 0)
            top = Math.Max(top, ((canopyMax - _spec.HeightBias) >> _cellShift) - 1);
        _topCell[(i + 1) * _heightGrid + (j + 1)] = top;
        _canopyLeaf[(i + 1) * _heightGrid + (j + 1)] = canopyId;
    }

    private void SampleSeparable(int i, int j, out int minHeight, ref int maxOwn)
    {
        // Vertical half of the min
        var wz = (j + 1) * _cellSize;
        minHeight = _xMin[wz];
        for (var w = 1; w < _windowSpan; w++)
            if (_xMin[wz + w] < minHeight) minHeight = _xMin[wz + w];

        // The uneroded per-cell max is NOT separable and is not meant to be: it reads
        // the cell's own footprint only (no margin), exactly as before, just out of the
        // grid - the GENERATED grid, so no edit moves the water or beach decisions.
        var ox = i * _cellSize + _fineMargin;
        var oz = j * _cellSize + _fineMargin;
        for (var dx = 0; dx < _cellSize; dx++)
        {
            var row = (ox + dx) * _fineSpan + oz;
            for (var dz = 0; dz < _cellSize; dz++)
                if (_fineGen[row + dz] > maxOwn) maxOwn = _fineGen[row + dz];
        }

        // Tile rule: an override's block shows only where that override actually is
        // the drawn (eroded-min) surface, so a lone block never repaints a coarse cell.
        // First match in dx-outer, dz-inner order, the seam path's order too.
        byte topBlock = 0;
        if (_hasRegionOverrides)
        {
            for (var dx = 0; dx < _cellSize && topBlock == 0; dx++)
            {
                var row = (ox + dx) * _fineSpan + oz;
                for (var dz = 0; dz < _cellSize; dz++)
                    if (_fine[row + dz] == minHeight && _fineBlock[row + dz] != 0)
                    {
                        topBlock = _fineBlock[row + dz];
                        break;
                    }
            }
        }
        _topBlockGrid[(i + 1) * _heightGrid + (j + 1)] = topBlock;
    }

    private void SampleSeam(int i, int j, out int minHeight, ref int maxOwn)
    {
        // Test seam (item C1): the original fused O(window^2) scan, kept so
        // LodHeightSamplingTests can compare full output against it.
        minHeight = int.MaxValue;
        for (var dx = -_margin; dx < _cellSize + _margin; dx++)
        for (var dz = -_margin; dz < _cellSize + _margin; dz++)
        {
            var fx = _baseFineX + i * _cellSize + dx;
            var fz = _baseFineZ + j * _cellSize + dz;
            var h = FineHeight(fx, fz, out _);
            if (h < minHeight) minHeight = h;
            var g = FineHeightGen(fx, fz);
            if (g > maxOwn && dx >= 0 && dx < _cellSize && dz >= 0 && dz < _cellSize) maxOwn = g;
        }

        // The same tile rule, restricted to the cell's own footprint.
        byte topBlock = 0;
        if (_hasRegionOverrides)
        {
            for (var dx = 0; dx < _cellSize && topBlock == 0; dx++)
            for (var dz = 0; dz < _cellSize; dz++)
            {
                var h = FineHeight(_baseFineX + i * _cellSize + dx, _baseFineZ + j * _cellSize + dz, out var b);
                if (h == minHeight && b != 0)
                {
                    topBlock = b;
                    break;
                }
            }
        }
        _topBlockGrid[(i + 1) * _heightGrid + (j + 1)] = topBlock;
    }

    private void ClassifyGenerated(int i, int j, int centerX, int centerZ, int maxOwn, ref int canopyMax, ref byte canopyId)
    {
        var cellBiome = TerrainGenerator.BiomeAt(centerX, centerZ);
        _biomeGrid[(i + 1) * _heightGrid + (j + 1)] = cellBiome;

        // A cell holds water only where the chunk mesher would actually flood it: an
        // ocean, or a lake/river bed. maxOwn is the uneroded cell max, matching the
        // beach sampling.
        var cellLevel = TerrainGenerator.WaterLevelAt(centerX, centerZ);
        if (maxOwn >= cellLevel) cellLevel = TerrainGenerator.NoWater;

        // An oasis: the same pure site the chunk fill and Compute read turns the
        // cell's material to grass and its pond to the oasis' own surface
        if (WorldFeatureConfig.OasesEnabled
            && OasisGenerator.TryGetDisc(centerX, centerZ, out _, out _, out var oasisSurface))
        {
            cellBiome = Biome.Forest;
            if (OasisGenerator.IsPond(centerX, centerZ)) cellLevel = oasisSurface;
        }
        _waterLevels[(i + 1) * _heightGrid + (j + 1)] = cellLevel;
        _seaFloor[(i + 1) * _heightGrid + (j + 1)] = FloorMaterial(maxOwn, cellLevel, cellBiome);

        // Sand rings ocean shores and lake/river rims, not every column in the height
        // band. Taiga shores wear sand like forest ones; frozen rock keeps its own
        // material.
        _beachTop[(i + 1) * _heightGrid + (j + 1)] = TerrainGenerator.IsBeachHeight(maxOwn)
            && (cellBiome == Biome.Ocean
                || ((cellBiome == Biome.Forest || cellBiome == Biome.Taiga)
                    && TerrainGenerator.SurfaceWaterWeight(centerX, centerZ) > 0f));
        _gravelTop[(i + 1) * _heightGrid + (j + 1)] =
            _beachTop[(i + 1) * _heightGrid + (j + 1)] && TerrainGenerator.IsGravelPatch(centerX, centerZ);
        _rockTop[(i + 1) * _heightGrid + (j + 1)] =
            cellBiome == Biome.Forest && maxOwn >= WorldGenerationConfig.MountainRockHeight;

        var cold = SnowGenerator.ColdWeightAt(centerX, centerZ, maxOwn);
        _snowTop[(i + 1) * _heightGrid + (j + 1)] = cold > WorldGenerationConfig.SnowColdThreshold;
        _iceTop[(i + 1) * _heightGrid + (j + 1)] = cold > WorldGenerationConfig.FreezeColdThreshold;

        for (var dx = 0; dx < _cellSize; dx++)
        for (var dz = 0; dz < _cellSize; dz++)
        {
            if (!TreeGenerator.TryGetCanopy(_baseFineX + i * _cellSize + dx, _baseFineZ + j * _cellSize + dz,
                    _spec.ProxyCanopy, out var t, out var leaf))
                continue;
            if (t > canopyMax)
            {
                canopyMax = t;
                canopyId = leaf;
            }
        }
    }

    private void ClassifySupplied(int i, int j, ColumnData suppliedColumn, int centerLocal, int maxOwn)
    {
        // A supplied column: every decision comes from the source's ColumnData, never
        // from worldgen, and its trees are already in its blocks (no canopy bumps).
        var cellBiome = suppliedColumn.Biomes[centerLocal];
        _biomeGrid[(i + 1) * _heightGrid + (j + 1)] = cellBiome;

        var cellLevel = suppliedColumn.WaterLevel[centerLocal];
        if (maxOwn >= cellLevel) cellLevel = TerrainGenerator.NoWater;
        _waterLevels[(i + 1) * _heightGrid + (j + 1)] = cellLevel;
        _seaFloor[(i + 1) * _heightGrid + (j + 1)] = FloorMaterial(maxOwn, cellLevel, cellBiome);

        _beachTop[(i + 1) * _heightGrid + (j + 1)] =
            TerrainGenerator.IsBeachHeight(maxOwn) && cellBiome == Biome.Ocean;
        _gravelTop[(i + 1) * _heightGrid + (j + 1)] = false;
        // The column-level bare-rock truth the chunk mesher's column fill follows, so a
        // supplied grass plateau above the mountain rock line keeps its grass.
        _rockTop[(i + 1) * _heightGrid + (j + 1)] =
            cellBiome == Biome.Forest && suppliedColumn.DirtDepth[centerLocal] == 0;
        _snowTop[(i + 1) * _heightGrid + (j + 1)] = cellBiome is Biome.Taiga or Biome.Frozen;
        _iceTop[(i + 1) * _heightGrid + (j + 1)] = cellBiome == Biome.Frozen;
    }

    private void FillBurialRanges(out int[] columnCyMin, out int[] columnCyMax)
    {
        // Which chunks must be drawn for a full-detail column to hide this shell: everything
        // from the lowest side face (set by the neighboring cells' tops, hence the 1-cell ring)
        // up to the top face and the real terrain surface above it. The low end reads the
        // ground grid (canopy bumps must not lift it, or shell sides would show with no chunk
        // behind them); the high end reads the bumped grid so the canopy is covered too.
        var cellsPerChunk = Chunk.Size / _cellSize;
        columnCyMin = new int[_regionChunkSpan * _regionChunkSpan];
        columnCyMax = new int[_regionChunkSpan * _regionChunkSpan];
        for (var ci = 0; ci < _regionChunkSpan; ci++)
        {
            for (var cj = 0; cj < _regionChunkSpan; cj++)
                FillBurialRange(ci, cj, cellsPerChunk, columnCyMin, columnCyMax);
        }
    }

    private void FillBurialRange(int ci, int cj, int cellsPerChunk, int[] columnCyMin, int[] columnCyMax)
    {
        var minTop = int.MaxValue;
        var maxTop = int.MinValue;
        for (var i = ci * cellsPerChunk - 1; i <= (ci + 1) * cellsPerChunk; i++)
        for (var j = cj * cellsPerChunk - 1; j <= (cj + 1) * cellsPerChunk; j++)
        {
            var g = _groundTop[(i + 1) * _heightGrid + (j + 1)];
            if (g < minTop) minTop = g;
            var t = _topCell[(i + 1) * _heightGrid + (j + 1)];
            if (t > maxTop) maxTop = t;
        }

        var maxHeight = int.MinValue;
        foreach (var h in _heightmaps[(ci + 1) * _chunkSpan + (cj + 1)])
            if (h > maxHeight) maxHeight = h;
        // A raised column (a structure built above the terrain) must be covered too before
        // the shell is judged buried, so the chunks holding its top have to be drawn.
        if (_overrideMaps?[(ci + 1) * _chunkSpan + (cj + 1)] is { } overrides)
            foreach (var packed in overrides)
            {
                if (packed == LodSurface.None) continue;
                LodSurface.Unpack(packed, out var top, out _);
                if (top > maxHeight) maxHeight = top;
            }

        // A water cell hides this shell only once the chunk holding its surface is meshed
        // too, or the sheet would vanish before the chunk water appears
        var maxWater = TerrainGenerator.NoWater;
        for (var i = ci * cellsPerChunk; i < (ci + 1) * cellsPerChunk; i++)
        for (var j = cj * cellsPerChunk; j < (cj + 1) * cellsPerChunk; j++)
        {
            var cellLevel = _waterLevels[(i + 1) * _heightGrid + (j + 1)];
            if (cellLevel > maxWater) maxWater = cellLevel;
        }

        var yLo = _cellSize * (minTop + 1);
        var yHi = Math.Max(_cellSize * (maxTop + 1), maxHeight + 1);
        if (maxWater > TerrainGenerator.NoWater) yHi = Math.Max(yHi, maxWater + 1);
        columnCyMin[ci * _regionChunkSpan + cj] = yLo >> Chunk.SizeShift;
        columnCyMax[ci * _regionChunkSpan + cj] = (yHi - 1) >> Chunk.SizeShift;
    }

    private static byte FloorMaterial(int height, int level, Biome biome) =>
        biome == Biome.Frozen ? (byte)2
        : biome == Biome.Ocean || level <= WorldGenerationConfig.SeaLevel
            ? height >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanSandDepth ? (byte)0
            : height >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanGravelDepth ? (byte)1
            : (byte)2
        : (byte)3;

    private LodMeshData EmitMesh(int level, int rx, int rz, MeshBufferPool? buffers, int[] columnCyMin, int[] columnCyMax)
    {
        var emitter = new LodFaceEmitter(buffers, _spec, _cellSize, _cells, _heightGrid, _regionChunkSpan, _chunkSpan,
            _latticeSpan, _regionChunkX, _regionChunkZ, _supplied, _columns, _grassTint, _foliageTint, _hasRegionOverrides,
            _topCell, _groundTop, _beachTop, _gravelTop, _rockTop, _snowTop, _iceTop, _biomeGrid, _waterLevels, _seaFloor,
            _canopyLeaf, _topBlockGrid);
        emitter.Emit();
        return emitter.ToMeshData(level, rx, rz, columnCyMin, columnCyMax);
    }

    private void ReturnRentals()
    {
        ArrayPool<int>.Shared.Return(_groundTop);
        ArrayPool<int>.Shared.Return(_topCell);
        ArrayPool<int>.Shared.Return(_waterLevels);
        ArrayPool<bool>.Shared.Return(_beachTop);
        ArrayPool<bool>.Shared.Return(_gravelTop);
        ArrayPool<bool>.Shared.Return(_snowTop);
        ArrayPool<bool>.Shared.Return(_iceTop);
        ArrayPool<bool>.Shared.Return(_rockTop);
        ArrayPool<Biome>.Shared.Return(_biomeGrid);
        ArrayPool<byte>.Shared.Return(_seaFloor);
        ArrayPool<byte>.Shared.Return(_canopyLeaf);
        ArrayPool<int>.Shared.Return(_fine);
        ArrayPool<int>.Shared.Return(_xMin);
        if (!ReferenceEquals(_fineGen, _fine)) ArrayPool<int>.Shared.Return(_fineGen);
        ArrayPool<byte>.Shared.Return(_fineBlock);
        ArrayPool<byte>.Shared.Return(_topBlockGrid);
        ArrayPool<Vector3>.Shared.Return(_grassTint);
        ArrayPool<Vector3>.Shared.Return(_foliageTint);
    }
}
