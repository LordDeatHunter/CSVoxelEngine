using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace silkgltriangle;

/// <summary>
/// Parameters for one far-terrain LOD ring. RegionBlocks and RegionRadius must keep
/// CellSize * (RegionBlocks / CellSize) == RegionBlocks with power-of-two sizes.
/// <para><c>ProxyCanopy</c>: whether the ring reads each tree species' <c>lod</c> crown proxy
/// (<see cref="TreeGenerator.TryGetCanopy(int, int, bool, out int, out byte)"/>) instead of the
/// grown canopy. The first ring meets the full-detail chunks and must match what they stamp;
/// every farther ring reads the proxy, so the far sweep never runs tree growth.</para>
/// </summary>
public readonly record struct LodLevelSpec(int CellSize, int RegionBlocks, int RegionRadius, int SampleMargin, int HeightBias,
    bool ProxyCanopy = false)
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
///
/// A region owns no GL objects of its own: both its meshes live in the render manager's shared
/// LOD <see cref="ChunkGeometryPool"/> as <see cref="GeometrySlice"/>s, and the three region
/// walks submit them through <see cref="ChunkDrawBatch"/> exactly as the chunk passes do — one
/// <c>glMultiDrawElementsIndirect</c> per pass instead of a bind, three uniforms and a draw per
/// surviving region (item R10).
/// </summary>
public class LodRegion : IDisposable
{
    // Level 0 is full-detail chunks (radius 16 chunks = 256 blocks); these rings extend past it.
    // Nesting invariants: SampleMargin(L+1) >= SampleMargin(L) + CellSize(L) - 1
    //                     HeightBias(L+1)   >= HeightBias(L) + CellSize(L)
    // The first ring reads the grown (batch) canopy the chunks stamp; the farther rings set
    // ProxyCanopy and read the species' lod crown proxy, so their sweep never grows a tree.
    public static readonly LodLevelSpec[] Levels =
    [
        new(CellSize: 2, RegionBlocks: 64, RegionRadius: 8, SampleMargin: 1, HeightBias: 0),   // to  512 blocks
        new(CellSize: 4, RegionBlocks: 128, RegionRadius: 8, SampleMargin: 3, HeightBias: 2, ProxyCanopy: true),  // to 1024 blocks
        new(CellSize: 8, RegionBlocks: 256, RegionRadius: 8, SampleMargin: 7, HeightBias: 6, ProxyCanopy: true),  // to 2048 blocks
    ];

    // Rent sizes for a region build's output lists. Every level is 32x32 cells (64/2, 128/4,
    // 256/8) and each cell emits a top quad plus a `top - neighborTop` side skirt per side, so
    // the opaque pair is sized from the measured working set rather than a guess: over each
    // level's full region ring the peak was 21476 vertices (L0), 18184 (L1), 11508 (L2). At a
    // 24576 rent a list grows 24576 -> 49152 -> 98304 at worst, and AddQuad refuses past
    // ~65532 vertices, so a grown list never passes MeshBufferPool's capacity*4 ceiling and no
    // real region is ever dropped instead of pooled. The sea sheet is one +Y quad per sea cell,
    // so 1024 quads = 4096 vertices / 6144 indices is the exact water worst case.
    public const int MeshVertexCapacity = 24576;
    public const int MeshIndexCapacity = 36864;
    public const int WaterVertexCapacity = 4096;
    public const int WaterIndexCapacity = 6144;

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

    /// <summary>
    /// This region's index in <c>LodManager._pendingCover</c>, or -1 while it is not in that
    /// list. Maintained by <see cref="LodManager"/> on the main thread only, exactly like
    /// <see cref="ListIndex"/>, so removal is a swap instead of a linear scan.
    /// </summary>
    public int PendingCoverIndex = -1;

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

    /// <summary>
    /// Where this region's terrain shell lives in the render manager's LOD
    /// <see cref="ChunkGeometryPool"/>, or <c>null</c> when the build produced no shell geometry.
    /// The LOD and mirror passes turn these into <c>glMultiDrawElementsIndirect</c> commands.
    /// </summary>
    public GeometrySlice? OpaqueSlice { get; private set; }

    /// <summary>
    /// Where this region's sea sheet lives in the same pool, or <c>null</c> when no cell of the
    /// region is below sea level. The sea sheet is a separate slice so the renderer can draw it
    /// blended, after everything opaque (see RenderManager.RenderWorldTranslucent).
    /// </summary>
    public GeometrySlice? WaterSlice { get; private set; }

    // The pool both slices came from, so Dispose can hand the space back without an unload
    // having to thread a pool reference in. One pool covers both meshes: the pair is born from
    // one LodMeshData and dies in the same Dispose, so their lifetimes never diverge.
    private ChunkGeometryPool? _pool;

    public bool HasMesh => OpaqueSlice != null;

    /// <summary>Whether any cell of this region is below sea level, i.e. it has a water sheet.</summary>
    public bool HasWaterMesh => WaterSlice != null;

    // Destroying blocks exposes the LOD buried beneath the surface. These per-column caps
    // force the LOD height below every removed block so the LOD always stays hidden.
    // They are one half of a pair: the cap has MIN semantics (it only ever falls, so a hole dug
    // below the surface can never expose the shell), while LodSurface's per-column override has
    // REPLACE semantics (the actual top height and block after a placement, a replacement or a
    // removal of the top). A fine column's LOD height is min(override ?? Heights, cap), and
    // BuildMeshData applies both before erosion and HeightBias, so the nesting invariants hold.
    // Bucketed by chunk column (item C9): the value is a Chunk.Size * Chunk.Size array of caps
    // indexed exactly like a heightmap (((x & SizeMask) << SizeShift) | (z & SizeMask)), NoCap
    // where the column is uncapped. A region build then resolves one bucket per prefetched
    // heightmap instead of hashing an (x, z) tuple per height sample, so an unedited region pays
    // nothing and an edited one pays one probe per chunk column, not one per sample.
    // Process-global but per WORLD: the caps record that world's edits, so WorldSeed.Apply resets
    // them between worlds (ResetColumnCaps), and a save carries them across a restart through
    // LodEditState (lod-edits.bin: ExportColumnCaps / ImportColumnCaps).
    private static readonly ConcurrentDictionary<(int CX, int CZ), int[]> ColumnCaps = new();

    /// <summary>
    /// Forgets every column cap, which belongs to the previous world's edits; only
    /// <see cref="WorldSeed.Apply"/> calls it.
    /// </summary>
    internal static void ResetColumnCaps() => ColumnCaps.Clear();

    // "Never capped". A cap is always a removed block's Y minus one, so int.MaxValue can never
    // compare below a real terrain height and the uncapped case needs no separate test.
    private const int NoCap = int.MaxValue;

    private static int[] NewCapBucket()
    {
        var bucket = new int[Chunk.Size * Chunk.Size];
        Array.Fill(bucket, NoCap);
        return bucket;
    }

    /// <summary>
    /// Caps a column's LOD height below a removed block, judged against worldgen's terrain
    /// (<see cref="GeneratedWorldSource"/>). Returns false when the LOD terrain already sat below
    /// the removed block and no region rebuild is needed.
    /// </summary>
    public static bool LowerColumnBelow(int x, int y, int z) =>
        LowerColumnBelow(GeneratedWorldSource.Instance, x, y, z);

    /// <summary>
    /// Caps a column's LOD height below a removed block, judged against the column's terrain
    /// height from <paramref name="source"/> (the world's <see cref="ChunkManager.WorldSource"/>)
    /// when it has no surface override. Returns false when the LOD terrain already sat below the
    /// removed block and no region rebuild is needed.
    /// </summary>
    public static bool LowerColumnBelow(IWorldSource source, int x, int y, int z)
    {
        var cx = x >> Chunk.SizeShift;
        var cz = z >> Chunk.SizeShift;
        var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
        // The column's current top: a surface override (a raised or re-scanned column) if it has
        // one, else the source's terrain height. The cap then applies on top, as before.
        var current = LodSurface.TopOrHeight(source, x, z);

        ColumnCaps.TryGetValue((cx, cz), out var bucket);
        if (bucket != null)
        {
            var cap = Volatile.Read(ref bucket[local]);
            if (cap < current) current = cap;   // NoCap never wins, so this is the old Math.Min
        }

        // Removed block was above the LOD-relevant terrain (e.g. a player-placed block)
        if (y > current)
            return false;

        // Caps are written on the main thread and read on LodGen. The bucket reference publishes
        // through the ConcurrentDictionary; Volatile.Write orders the element write behind it, which
        // is strictly stronger than the plain dictionary write this replaces, so a future
        // multi-threaded LodGen (item C5) stays safe.
        bucket ??= ColumnCaps.GetOrAdd((cx, cz), static _ => NewCapBucket());
        Volatile.Write(ref bucket[local], y - 1);
        Interlocked.Increment(ref _capEditVersion);
        return true;
    }

    private static int _capEditVersion;

    /// <summary>Bumped by every cap <see cref="LowerColumnBelow"/> writes, so a save can tell whether anything changed.</summary>
    internal static int CapEditVersion => Volatile.Read(ref _capEditVersion);

    /// <summary>
    /// A copy of every cap bucket that caps at least one column, for the per-world LOD edit save
    /// (<c>LodEditState</c>). Main thread (the only writer), so the copies are consistent.
    /// </summary>
    internal static List<(int CX, int CZ, int[] Values)> ExportColumnCaps()
    {
        var result = new List<(int CX, int CZ, int[] Values)>();
        foreach (var (key, bucket) in ColumnCaps)
        {
            var copy = new int[bucket.Length];
            var any = false;
            for (var i = 0; i < copy.Length; i++)
            {
                copy[i] = Volatile.Read(ref bucket[i]);
                any |= copy[i] != NoCap;
            }

            if (any) result.Add((key.CX, key.CZ, copy));
        }

        return result;
    }

    /// <summary>
    /// Merges saved caps into the live table. Min semantics like every cap write: the lower cap
    /// wins, so an import can never raise a live edit's cap. Call before <c>LodManager.Start</c> so
    /// the first region builds see it; regions built later pick it up on their own. Leaves
    /// <see cref="CapEditVersion"/> alone: the imported caps are already what the file holds.
    /// </summary>
    internal static void ImportColumnCaps(IEnumerable<(int CX, int CZ, int[] Values)> caps)
    {
        foreach (var (cx, cz, values) in caps)
        {
            int[]? bucket = null;
            var n = Math.Min(values.Length, Chunk.Size * Chunk.Size);
            for (var i = 0; i < n; i++)
            {
                var cap = values[i];
                if (cap == NoCap) continue;

                bucket ??= ColumnCaps.GetOrAdd((cx, cz), static _ => NewCapBucket());
                if (cap < Volatile.Read(ref bucket[i]))
                    Volatile.Write(ref bucket[i], cap);
            }
        }
    }

    /// <summary>
    /// Draw order for the opaque LOD pass, as a plain scalar comparison so it can be pinned
    /// headlessly (see <c>LodDrawOrderTests</c>). Since R10 the LOD pass submits through
    /// <see cref="ChunkDrawBatch"/>, whose <c>glMultiDrawElementsIndirect</c> executes commands
    /// in array order, so this ordering IS the submission order and is load-bearing.
    ///
    /// Coarsest level first, then front to back within a level; where levels still
    /// overlap (a coarse region only partly covered by finer ones) finer geometry
    /// draws over the coarser mesh buried beneath it
    /// </summary>
    public static int CompareOpaqueDraw(int levelA, float distSqA, int levelB, float distSqB)
        => levelA != levelB
            ? levelB.CompareTo(levelA)
            : distSqA.CompareTo(distSqB);

    /// <summary>
    /// Draw order for the LOD water sheets, as a plain scalar comparison so it can be pinned
    /// headlessly (see <c>LodDrawOrderTests</c>). Deliberately the opposite level order to
    /// <see cref="CompareOpaqueDraw"/>; like it, this is the submission order since R10.
    ///
    /// Finest level first: its sheet sits highest (each coarser level sinks by its own
    /// height bias), so the duplicates underneath it fail the depth test instead of
    /// double-blending the same stretch of sea
    /// </summary>
    public static int CompareWaterDraw(int levelA, float distSqA, int levelB, float distSqB)
        => levelA != levelB
            ? levelA.CompareTo(levelB)
            : distSqB.CompareTo(distSqA);

    /// <param name="pool">
    /// The shared LOD geometry pool both meshes upload into. Null is allowed only when every
    /// vertex list in <paramref name="data"/> is empty, which is the headless-test case: a
    /// non-empty mesh with no pool throws, exactly as <c>Chunk.UpdateMesh</c> does.
    /// </param>
    /// <param name="data">A finished background build (see <see cref="BuildMeshData"/>).</param>
    public LodRegion(ChunkGeometryPool? pool, LodMeshData data)
    {
        Level = data.Level;
        X = data.X;
        Z = data.Z;
        SizeBlocks = Levels[data.Level].RegionBlocks;
        YMin = data.YMin;
        YMax = data.YMax;
        ColumnCyMin = data.ColumnCyMin;
        ColumnCyMax = data.ColumnCyMax;

        if (data.Vertices.Count > 0)
        {
            _pool = pool ?? throw new InvalidOperationException(
                "non-empty LOD region mesh without a geometry pool");
            OpaqueSlice = _pool.Upload(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data.Vertices),
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data.Indices));
        }

        if (data.WaterVertices.Count > 0)
        {
            _pool = pool ?? throw new InvalidOperationException(
                "non-empty LOD region mesh without a geometry pool");
            WaterSlice = _pool.Upload(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data.WaterVertices),
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data.WaterIndices));
        }
    }

    /// <summary>
    /// Hands both slices back to the pool they came from and forgets them. Freeing twice
    /// throws inside the allocator, so each slice is nulled the moment it is released.
    /// </summary>
    private void ReleaseSlices()
    {
        if (OpaqueSlice is { } opaque)
        {
            _pool?.Free(opaque);
            OpaqueSlice = null;
        }

        if (WaterSlice is { } water)
        {
            _pool?.Free(water);
            WaterSlice = null;
        }

        _pool = null;
    }

    public void Dispose()
    {
        ReleaseSlices();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Builds a region mesh on a background thread (CPU only, no GL calls).
    /// Vertices are local in X/Z (relative to the region origin) and absolute in Y.
    ///
    /// The four output lists are rented from <paramref name="buffers"/> — a
    /// <see cref="MeshBufferPool"/> sized at <see cref="MeshVertexCapacity"/> /
    /// <see cref="WaterVertexCapacity"/> — and travel out inside the returned
    /// <see cref="LodMeshData"/>; this method never returns them, because a completed build can
    /// wait in the upload queue across frames. <see cref="LodManager"/> hands them back on the
    /// main thread once the region is on the GPU, or when a finished build is dropped out of
    /// range. Production callers must always pass the pool; the parameter is optional only so
    /// headless tests can build a region standalone, in which case the lists are plain
    /// allocations and nothing has to be returned.
    ///
    /// <paramref name="separableMin"/> is the test seam for item C1: false runs the original
    /// fused nested-window scan instead of the separable rectangular min, and
    /// <c>LodHeightSamplingTests</c> asserts the two produce bit-identical output. Production
    /// always leaves it true.
    ///
    /// <paramref name="source"/> is the world's <see cref="IWorldSource"/> (null means worldgen,
    /// <see cref="GeneratedWorldSource.Instance"/>). Every chunk column's heights come from its
    /// <see cref="IWorldSource.GetColumn"/>. For a column the source does not supply, every other
    /// per-cell decision stays the worldgen read it always was (the undithered
    /// <see cref="TerrainGenerator.BiomeAt"/>, <see cref="TerrainGenerator.WaterLevelAt"/>, the
    /// shore weight and gravel patches, <see cref="TerrainGenerator.ColdWeight"/>, the canopy and
    /// the climate tint lattice), so a generated region is byte-identical to the build without a
    /// source. For a column the source supplies (<see cref="IWorldSource.Supplies"/>), those come
    /// from its <see cref="ColumnData"/> instead: the biome and water level at the cell centre,
    /// snow from a Taiga or Frozen biome and sea ice from a Frozen one, beaches from
    /// <see cref="TerrainGenerator.IsBeachHeight"/> in the Ocean biome, bare rock from a zero
    /// <see cref="ColumnData.DirtDepth"/> in the Forest biome, and the tint lattice from the
    /// stored corner climate. A supplied column's far LOD therefore shows no canopy bumps (its
    /// trees are in its blocks) and no gravel caps.
    /// </summary>
    public static LodMeshData BuildMeshData(int level, int rx, int rz, MeshBufferPool? buffers = null,
        bool separableMin = true, IWorldSource? source = null)
    {
        source ??= GeneratedWorldSource.Instance;
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
        var hasCaps = !ColumnCaps.IsEmpty;
        var hasOverrides = !LodSurface.IsEmpty;
        var heightmaps = new int[chunkSpan * chunkSpan][];
        // The source's column and whether it supplies it, per prefetched chunk column (same shape
        // as heightmaps). A supplied column's cell decisions read its ColumnData; anySupplied keeps
        // a region the source supplies nothing of on the exact worldgen path.
        var columns = new ColumnData[chunkSpan * chunkSpan];
        var supplied = new bool[chunkSpan * chunkSpan];
        var anySupplied = false;
        // One cap-bucket resolution per prefetched heightmap (item C9). Null for every chunk column
        // the player has never edited — which is all of them until the first mined block — and the
        // whole array is skipped while the table is empty. Same shape and lifetime as heightmaps.
        var capmaps = hasCaps ? new int[chunkSpan * chunkSpan][] : null;
        // The LodSurface override buckets, resolved the same way and with the same shape.
        var overrideMaps = hasOverrides ? new int[chunkSpan * chunkSpan][] : null;
        // Whether any chunk column this region samples carries an edit. Only then does the build
        // pay for the generated-only grid and the tile rule; an unedited region keeps the old path.
        var hasEdits = false;
        var hasRegionOverrides = false;
        for (var cx = 0; cx < chunkSpan; cx++)
        for (var cz = 0; cz < chunkSpan; cz++)
        {
            var column = source.GetColumn(baseChunkX + cx, baseChunkZ + cz);
            columns[cx * chunkSpan + cz] = column;
            heightmaps[cx * chunkSpan + cz] = column.Heights;
            supplied[cx * chunkSpan + cz] = source.Supplies(baseChunkX + cx, baseChunkZ + cz);
            anySupplied |= supplied[cx * chunkSpan + cz];
            if (capmaps != null && ColumnCaps.TryGetValue((baseChunkX + cx, baseChunkZ + cz), out var bucket))
            {
                capmaps[cx * chunkSpan + cz] = bucket;
                hasEdits = true;
            }

            if (overrideMaps != null && LodSurface.BucketOf(baseChunkX + cx, baseChunkZ + cz) is { } overrides)
            {
                overrideMaps[cx * chunkSpan + cz] = overrides;
                hasEdits = true;
                hasRegionOverrides = true;
            }
        }

        var baseFineX = rx * spec.RegionBlocks;
        var baseFineZ = rz * spec.RegionBlocks;

        // Fine-height grid covering the union of every cell's sample window: the region grown by
        // cellSize + margin on each side - the same reach LodManager.MarkEditedColumn uses. Filling
        // it chunk column by chunk column out of the prefetched heightmaps turns each sample into a
        // contiguous copy (the per-sample chunk-index math FineHeight does disappears) and applies
        // the C9 cap bucket and the LodSurface override bucket once per chunk column as it copies.
        var fineMargin = cellSize + margin;                  // 3 / 7 / 15, always < Chunk.Size
        var fineSpan = spec.RegionBlocks + 2 * fineMargin;   // 70 / 142 / 286
        var fineOriginX = baseFineX - fineMargin;
        var fineOriginZ = baseFineZ - fineMargin;
        var windowSpan = cellSize + 2 * margin;              // 4 / 10 / 22

        // The seam path's per-sample read of the EDITED height: the override replaces the generated
        // height, then the cap lowers it, exactly the order the separable fill applies. block is
        // the override's block while that override is the column's drawn height, else 0.
        int FineHeight(int fineX, int fineZ, out byte block)
        {
            var cx = (fineX >> Chunk.SizeShift) - baseChunkX;
            var cz = (fineZ >> Chunk.SizeShift) - baseChunkZ;
            var local = ((fineX & Chunk.SizeMask) << Chunk.SizeShift) | (fineZ & Chunk.SizeMask);
            var height = heightmaps[cx * chunkSpan + cz][local];
            block = 0;

            var overrides = overrideMaps?[cx * chunkSpan + cz];
            var packed = overrides != null ? overrides[local] : LodSurface.None;
            if (packed != LodSurface.None) LodSurface.Unpack(packed, out height, out block);

            var caps = capmaps?[cx * chunkSpan + cz];
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
            var cx = (fineX >> Chunk.SizeShift) - baseChunkX;
            var cz = (fineZ >> Chunk.SizeShift) - baseChunkZ;
            var local = ((fineX & Chunk.SizeMask) << Chunk.SizeShift) | (fineZ & Chunk.SizeMask);
            return heightmaps[cx * chunkSpan + cz][local];
        }

        // Top solid cell per LOD column (with 1-cell border): the eroded terrain below, the
        // canopy above it, and their max — the shell actually drawn. Canopy is sampled over
        // each cell's own footprint only (no erosion margin), so a tree lifts just its own
        // column into a leaf-textured bump instead of a plateau. Rented: a region build would
        // otherwise allocate five grids per region on the worker.
        var gridSize = heightGrid * heightGrid;
        var groundTop = ArrayPool<int>.Shared.Rent(gridSize);
        var topCell = ArrayPool<int>.Shared.Rent(gridSize);
        // The level each cell's water sits at (TerrainGenerator.NoWater where it holds none), read
        // at the cell centre. Wetness is measured as the max over the cell's OWN footprint, with
        // no erosion margin: the eroded minimum would paint water across every beach whose
        // neighbourhood dips below the waterline. A level rather than a flag, because a lake's
        // sheet sits at the lake's own surface instead of at sea level.
        var waterLevels = ArrayPool<int>.Shared.Rent(gridSize);
        // Beaches and gravel caps read from the same uneroded max for the same reason, the
        // gravel with one noise eval at the cell centre per cell.
        var beachTop = ArrayPool<bool>.Shared.Rent(gridSize);
        var gravelTop = ArrayPool<bool>.Shared.Rent(gridSize);
        // Cold cells: snow covers their land surface, and the coldest freeze the sea sheet on top
        // of the sea. Both read ColdWeight so the far snow and ice lines match the near chunks.
        var snowTop = ArrayPool<bool>.Shared.Rent(gridSize);
        var iceTop = ArrayPool<bool>.Shared.Rent(gridSize);
        // Bare-rock mountain tops, matching the chunk mesher's rock line, so a distant peak
        // shows the same stone cap the near chunks do.
        var rockTop = ArrayPool<bool>.Shared.Rent(gridSize);
        var biomeGrid = ArrayPool<Biome>.Shared.Rent(gridSize);
        var seaFloor = ArrayPool<byte>.Shared.Rent(gridSize);
        var canopyLeaf = ArrayPool<byte>.Shared.Rent(gridSize);
        // Item C1: the fine heights every cell window reads (index fx * fineSpan + fz), and one row
        // of the separable min's horizontal half, rebuilt per cell row and reused by every column.
        // Level 2 is the largest at 81796 ints, well inside ArrayPool<int>.Shared's largest bucket.
        // Both are rented unconditionally, seam path included, so the finally has no condition.
        var fine = ArrayPool<int>.Shared.Rent(fineSpan * fineSpan);
        var xMin = ArrayPool<int>.Shared.Rent(fineSpan);
        // Edited regions only: the GENERATED fine heights (no override, no cap), which the per-cell
        // uneroded max reads so water, sea floor, beach and rock stay the generated decisions — a
        // pillar in the shallows must never delete the sea sheet. An unedited region aliases it to
        // `fine`, which is then the same numbers, and pays for no second grid.
        var fineGen = hasEdits ? ArrayPool<int>.Shared.Rent(fineSpan * fineSpan) : fine;
        // The override block of each fine column while that override is its drawn height (0
        // otherwise), and the block each cell's drawn surface wears by the tile rule below (0 =
        // the generated tile chain). Only read while the region has overrides.
        var fineBlock = ArrayPool<byte>.Shared.Rent(fineSpan * fineSpan);
        var topBlockGrid = ArrayPool<byte>.Shared.Rent(gridSize);
        // The biome tint lattice (BiomeTint) at this region's chunk-column corners, both palettes
        // from one climate evaluation per point. Indexed from the region's OWN first chunk column,
        // never from baseChunkX, which is the prefetch origin one chunk before it: the chunk mesher
        // samples BiomeTint.FieldFor(cx, cz) of the chunk a vertex stands in, so the LOD has to read
        // the very same lattice points or its grass would shift a chunk against the near terrain.
        var regionChunkX = rx * regionChunkSpan;   // = baseChunkX + 1
        var regionChunkZ = rz * regionChunkSpan;   // = baseChunkZ + 1
        var latticeSpan = regionChunkSpan + 1;
        var grassTint = ArrayPool<Vector3>.Shared.Rent(latticeSpan * latticeSpan);
        var foliageTint = ArrayPool<Vector3>.Shared.Rent(latticeSpan * latticeSpan);
        try
        {
        if (separableMin)
        {
            // Every grid element is written before it is read: the prefetched chunk columns span
            // fine X [baseFineX - 16, baseFineX + RegionBlocks + 16), which strictly contains the
            // grid's [baseFineX - fineMargin, baseFineX + RegionBlocks + fineMargin) because
            // fineMargin <= 15. ArrayPool hands back uncleared arrays, so that total coverage is
            // load-bearing - narrowing this fill would silently read a previous region's heights.
            for (var cx = 0; cx < chunkSpan; cx++)
            for (var cz = 0; cz < chunkSpan; cz++)
            {
                var heights = heightmaps[cx * chunkSpan + cz];
                var caps = capmaps?[cx * chunkSpan + cz];
                var overrides = overrideMaps?[cx * chunkSpan + cz];
                var wx0 = (baseChunkX + cx) << Chunk.SizeShift;
                var wz0 = (baseChunkZ + cz) << Chunk.SizeShift;
                var lx0 = Math.Max(0, fineOriginX - wx0);
                var lx1 = Math.Min(Chunk.Size, fineOriginX + fineSpan - wx0);
                var lz0 = Math.Max(0, fineOriginZ - wz0);
                var lz1 = Math.Min(Chunk.Size, fineOriginZ + fineSpan - wz0);
                for (var lx = lx0; lx < lx1; lx++)
                {
                    var src = lx << Chunk.SizeShift;
                    var dst = (wx0 + lx - fineOriginX) * fineSpan + (wz0 - fineOriginZ);
                    // Split on edits so the unedited case - every chunk column in an unedited
                    // world - stays a branch-free copy.
                    if (caps == null && overrides == null)
                    {
                        for (var lz = lz0; lz < lz1; lz++)
                            fine[dst + lz] = heights[src + lz];
                        if (!ReferenceEquals(fineGen, fine))
                            Array.Copy(fine, dst + lz0, fineGen, dst + lz0, lz1 - lz0);
                        // ArrayPool hands back uncleared arrays, so a clean column says so.
                        if (hasRegionOverrides)
                            Array.Clear(fineBlock, dst + lz0, lz1 - lz0);
                    }
                    else
                        for (var lz = lz0; lz < lz1; lz++)
                        {
                            // Override first (replace), then the cap (min), as FineHeight does.
                            var h = heights[src + lz];
                            fineGen[dst + lz] = h;
                            byte b = 0;
                            var o = overrides != null ? overrides[src + lz] : LodSurface.None;
                            if (o != LodSurface.None) LodSurface.Unpack(o, out h, out b);
                            var c = caps != null ? caps[src + lz] : NoCap;
                            if (c < h)
                            {
                                h = c;
                                b = 0;   // a cap below the override: the override is not what shows
                            }
                            fine[dst + lz] = h;
                            fineBlock[dst + lz] = b;
                        }
                }
            }
        }

        // Cell row i's X window starts at grid offset i * cellSize - margin + fineMargin, which is
        // exactly (i + 1) * cellSize because fineMargin == cellSize + margin: 0 at i == -1 and
        // RegionBlocks + cellSize at i == cells, whose window end + windowSpan is exactly fineSpan.
        // Nothing indexes outside the grid and no clamping is involved - the grid is sized to the
        // one-cell overrun rather than clamped against it.
        for (var i = -1; i <= cells; i++)
        {
            if (separableMin)
            {
                // Horizontal half of the separable rectangular min: one minimum per fine Z over
                // this cell row's X window, reused by every j below. Integer min is associative and
                // commutative, so min(window) == min over Z of (min over X) - bit-identical to the
                // nested scan, including the i == -1 / i == cells rows that reach one cell past the
                // region. Both accesses are stride 1 and the whole grid is L2-resident, and
                // adjacent cell rows re-read windowSpan - cellSize of the same rows.
                var wx = (i + 1) * cellSize;
                Array.Copy(fine, wx * fineSpan, xMin, 0, fineSpan);
                for (var w = 1; w < windowSpan; w++)
                {
                    var row = fine.AsSpan((wx + w) * fineSpan, fineSpan);
                    var acc = xMin.AsSpan(0, fineSpan);
                    for (var fz = 0; fz < fineSpan; fz++)
                        if (row[fz] < acc[fz]) acc[fz] = row[fz];
                }
            }

            for (var j = -1; j <= cells; j++)
            {
                int minHeight;
                var maxOwn = int.MinValue;
                if (separableMin)
                {
                    // Vertical half of the min
                    var wz = (j + 1) * cellSize;
                    minHeight = xMin[wz];
                    for (var w = 1; w < windowSpan; w++)
                        if (xMin[wz + w] < minHeight) minHeight = xMin[wz + w];

                    // The uneroded per-cell max is NOT separable and is not meant to be: it reads
                    // the cell's own footprint only (no margin), exactly as before, just out of the
                    // grid - the GENERATED grid, so no edit moves the water or beach decisions.
                    var ox = i * cellSize + fineMargin;
                    var oz = j * cellSize + fineMargin;
                    for (var dx = 0; dx < cellSize; dx++)
                    {
                        var row = (ox + dx) * fineSpan + oz;
                        for (var dz = 0; dz < cellSize; dz++)
                            if (fineGen[row + dz] > maxOwn) maxOwn = fineGen[row + dz];
                    }

                    // Tile rule: an override's block shows only where that override actually is
                    // the drawn (eroded-min) surface, so a lone block never repaints a coarse cell.
                    // First match in dx-outer, dz-inner order, the seam path's order too.
                    byte topBlock = 0;
                    if (hasRegionOverrides)
                    {
                        for (var dx = 0; dx < cellSize && topBlock == 0; dx++)
                        {
                            var row = (ox + dx) * fineSpan + oz;
                            for (var dz = 0; dz < cellSize; dz++)
                                if (fine[row + dz] == minHeight && fineBlock[row + dz] != 0)
                                {
                                    topBlock = fineBlock[row + dz];
                                    break;
                                }
                        }
                    }
                    topBlockGrid[(i + 1) * heightGrid + (j + 1)] = topBlock;
                }
                else
                {
                    // Test seam (item C1): the original fused O(window^2) scan, kept so
                    // LodHeightSamplingTests can compare full output against it.
                    minHeight = int.MaxValue;
                    for (var dx = -margin; dx < cellSize + margin; dx++)
                    for (var dz = -margin; dz < cellSize + margin; dz++)
                    {
                        var fx = baseFineX + i * cellSize + dx;
                        var fz = baseFineZ + j * cellSize + dz;
                        var h = FineHeight(fx, fz, out _);
                        if (h < minHeight) minHeight = h;
                        var g = FineHeightGen(fx, fz);
                        if (g > maxOwn && dx >= 0 && dx < cellSize && dz >= 0 && dz < cellSize) maxOwn = g;
                    }

                    // The same tile rule, restricted to the cell's own footprint.
                    byte topBlock = 0;
                    if (hasRegionOverrides)
                    {
                        for (var dx = 0; dx < cellSize && topBlock == 0; dx++)
                        for (var dz = 0; dz < cellSize; dz++)
                        {
                            var h = FineHeight(baseFineX + i * cellSize + dx, baseFineZ + j * cellSize + dz, out var b);
                            if (h == minHeight && b != 0)
                            {
                                topBlock = b;
                                break;
                            }
                        }
                    }
                    topBlockGrid[(i + 1) * heightGrid + (j + 1)] = topBlock;
                }

                // Biome read at the cell centre, like the gravel patch below: the coarse cell
                // wears one material even where its footprint straddles a border.
                var centreX = baseFineX + i * cellSize + cellSize / 2;
                var centreZ = baseFineZ + j * cellSize + cellSize / 2;

                // The column the cell centre stands in, when the source supplies it. Cells are
                // aligned to chunk columns (cellSize divides Chunk.Size), so the whole footprint
                // stands in it, and the 1-cell border stays inside the prefetched span exactly as
                // FineHeight's reads do.
                ColumnData? suppliedColumn = null;
                var centreLocal = 0;
                if (anySupplied)
                {
                    var k = ((centreX >> Chunk.SizeShift) - baseChunkX) * chunkSpan
                        + ((centreZ >> Chunk.SizeShift) - baseChunkZ);
                    if (supplied[k])
                    {
                        suppliedColumn = columns[k];
                        centreLocal = ((centreX & Chunk.SizeMask) << Chunk.SizeShift) | (centreZ & Chunk.SizeMask);
                    }
                }

                // Cell cy is solid iff cellSize * (cy + 1) <= eroded height
                var ground = ((minHeight - spec.HeightBias) >> cellShift) - 1;
                groundTop[(i + 1) * heightGrid + (j + 1)] = ground;

                // Canopy tops are always positive where present (trees need ground above the
                // minimum height), so 0 unambiguously means open sky. It must not reach the
                // cell formula: "no canopy" computes to cell -1, which would outrank deeply
                // negative terrain and pave lowlands with phantom leaf pillars.
                var top = ground;
                var canopyMax = 0;
                byte canopyId = 0;

                if (suppliedColumn == null)
                {
                    var cellBiome = TerrainGenerator.BiomeAt(centreX, centreZ);
                    biomeGrid[(i + 1) * heightGrid + (j + 1)] = cellBiome;

                    // A cell holds water only where the chunk mesher would actually flood it: an
                    // ocean, or a lake/river bed. maxOwn is the uneroded cell max, matching the
                    // beach sampling.
                    var cellLevel = TerrainGenerator.WaterLevelAt(centreX, centreZ);
                    if (maxOwn >= cellLevel) cellLevel = TerrainGenerator.NoWater;
                    waterLevels[(i + 1) * heightGrid + (j + 1)] = cellLevel;
                    seaFloor[(i + 1) * heightGrid + (j + 1)] = FloorMaterial(maxOwn, cellLevel, cellBiome);

                    // Sand rings ocean shores and lake/river rims, not every column in the height
                    // band. Taiga shores wear sand like forest ones; frozen rock keeps its own
                    // material.
                    beachTop[(i + 1) * heightGrid + (j + 1)] = TerrainGenerator.IsBeachHeight(maxOwn)
                        && (cellBiome == Biome.Ocean
                            || ((cellBiome == Biome.Forest || cellBiome == Biome.Taiga)
                                && TerrainGenerator.SurfaceWaterWeight(centreX, centreZ) > 0f));
                    gravelTop[(i + 1) * heightGrid + (j + 1)] =
                        beachTop[(i + 1) * heightGrid + (j + 1)] && TerrainGenerator.IsGravelPatch(centreX, centreZ);
                    rockTop[(i + 1) * heightGrid + (j + 1)] =
                        cellBiome == Biome.Forest && maxOwn >= WorldGenerationConfig.MountainRockHeight;

                    var cold = SnowGenerator.ColdWeightAt(centreX, centreZ, maxOwn);
                    snowTop[(i + 1) * heightGrid + (j + 1)] = cold > WorldGenerationConfig.SnowColdThreshold;
                    iceTop[(i + 1) * heightGrid + (j + 1)] = cold > WorldGenerationConfig.FreezeColdThreshold;

                    for (var dx = 0; dx < cellSize; dx++)
                    for (var dz = 0; dz < cellSize; dz++)
                    {
                        if (!TreeGenerator.TryGetCanopy(baseFineX + i * cellSize + dx, baseFineZ + j * cellSize + dz,
                                spec.ProxyCanopy, out var t, out var leaf))
                            continue;
                        if (t > canopyMax)
                        {
                            canopyMax = t;
                            canopyId = leaf;
                        }
                    }
                }
                else
                {
                    // A supplied column: every decision comes from the source's ColumnData, never
                    // from worldgen, and its trees are already in its blocks (no canopy bumps).
                    var cellBiome = suppliedColumn.Biomes[centreLocal];
                    biomeGrid[(i + 1) * heightGrid + (j + 1)] = cellBiome;

                    var cellLevel = suppliedColumn.WaterLevel[centreLocal];
                    if (maxOwn >= cellLevel) cellLevel = TerrainGenerator.NoWater;
                    waterLevels[(i + 1) * heightGrid + (j + 1)] = cellLevel;
                    seaFloor[(i + 1) * heightGrid + (j + 1)] = FloorMaterial(maxOwn, cellLevel, cellBiome);

                    beachTop[(i + 1) * heightGrid + (j + 1)] =
                        TerrainGenerator.IsBeachHeight(maxOwn) && cellBiome == Biome.Ocean;
                    gravelTop[(i + 1) * heightGrid + (j + 1)] = false;
                    // The column-level bare-rock truth the chunk mesher's column fill follows, so a
                    // supplied grass plateau above the mountain rock line keeps its grass.
                    rockTop[(i + 1) * heightGrid + (j + 1)] =
                        cellBiome == Biome.Forest && suppliedColumn.DirtDepth[centreLocal] == 0;
                    snowTop[(i + 1) * heightGrid + (j + 1)] = cellBiome is Biome.Taiga or Biome.Frozen;
                    iceTop[(i + 1) * heightGrid + (j + 1)] = cellBiome == Biome.Frozen;
                }

                if (canopyMax > 0)
                    top = Math.Max(top, ((canopyMax - spec.HeightBias) >> cellShift) - 1);
                topCell[(i + 1) * heightGrid + (j + 1)] = top;
                canopyLeaf[(i + 1) * heightGrid + (j + 1)] = canopyId;
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
                // A raised column (a structure built above the terrain) must be covered too before
                // the shell is judged buried, so the chunks holding its top have to be drawn.
                if (overrideMaps?[(ci + 1) * chunkSpan + (cj + 1)] is { } overrides)
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
                    var cellLevel = waterLevels[(i + 1) * heightGrid + (j + 1)];
                    if (cellLevel > maxWater) maxWater = cellLevel;
                }

                var yLo = cellSize * (minTop + 1);
                var yHi = Math.Max(cellSize * (maxTop + 1), maxHeight + 1);
                if (maxWater > TerrainGenerator.NoWater) yHi = Math.Max(yHi, maxWater + 1);
                columnCyMin[ci * regionChunkSpan + cj] = yLo >> Chunk.SizeShift;
                columnCyMax[ci * regionChunkSpan + cj] = (yHi - 1) >> Chunk.SizeShift;
            }
        }

        var grass = BlockType.ById[TerrainGenerator.SurfaceBlock]!;
        var dirt = BlockType.ById[TerrainGenerator.SoilBlock]!;
        var stone = BlockType.ById[TerrainGenerator.RockBlock]!;
        var sand = BlockType.ById[TerrainGenerator.SandBlock]!;
        var gravel = BlockType.ById[TerrainGenerator.GravelBlock]!;
        var sandstone = BlockType.ById[TerrainGenerator.SandstoneBlock]!;
        var fallbackLeaves = BlockType.ById[TreeGenerator.LeavesBlock]!;
        var snow = BlockType.ById[TerrainGenerator.SnowBlock]!;
        var ice = BlockType.ById[TerrainGenerator.IceBlock]!;

        BlockType CanopyBlock(byte leafId) =>
            (leafId != 0 ? BlockType.ById[leafId] : null) ?? fallbackLeaves;

        // Ocean floor block by the depth band the chunk mesher's GenerateColumn would pick for the
        // same height (0 sand, 1 gravel, 2 stone), so far seabed matches near seabed. A land biome's
        // lake or river bed keeps the soil the column wears under water instead (3), which is dirt.
        BlockType FloorBlock(byte material) => material switch
        {
            0 => sand,
            1 => gravel,
            3 => dirt,
            _ => stone,
        };

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

        // The tint at a region-local position, interpolated over the lattice cell of the chunk
        // column it stands in exactly as the chunk mesher's BiomeTintField.Sample does. The far edge
        // (x == RegionBlocks) reads the last column at fx = 1, which the weighted-sum Bilinear makes
        // bit-identical to the next region's first column at fx = 0.
        Vector3 SampleTint(BiomeTintPalette palette, Vector3 regionLocalPos)
        {
            var grid = palette == BiomeTintPalette.Foliage ? foliageTint : grassTint;
            var cx = Math.Clamp((int)regionLocalPos.X >> Chunk.SizeShift, 0, regionChunkSpan - 1);
            var cz = Math.Clamp((int)regionLocalPos.Z >> Chunk.SizeShift, 0, regionChunkSpan - 1);
            var fx = regionLocalPos.X / Chunk.Size - cx;
            var fz = regionLocalPos.Z / Chunk.Size - cz;
            return BiomeTint.Bilinear(
                grid[cx * latticeSpan + cz], grid[(cx + 1) * latticeSpan + cz],
                grid[cx * latticeSpan + cz + 1], grid[(cx + 1) * latticeSpan + cz + 1], fx, fz);
        }

        byte FloorMaterial(int height, int level, Biome biome) =>
            biome == Biome.Frozen ? (byte)2
            : biome == Biome.Ocean || level <= WorldGenerationConfig.SeaLevel
                ? height >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanSandDepth ? (byte)0
                : height >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanGravelDepth ? (byte)1
                : (byte)2
            : (byte)3;

        // Rented, not allocated: LodManager returns them on the main thread once the region is on
        // the GPU, or when a finished build is dropped out of range — never here, on the LodGen
        // worker, because a completed LodMeshData can wait in the upload queue across frames.
        var vertices = buffers?.RentVertices() ?? new List<Vertex>(MeshVertexCapacity);
        var indices = buffers?.RentIndices() ?? new List<ushort>(MeshIndexCapacity);
        var waterVertices = buffers?.RentTranslucentVertices() ?? new List<Vertex>(WaterVertexCapacity);
        var waterIndices = buffers?.RentTranslucentIndices() ?? new List<ushort>(WaterIndexCapacity);
        var yMin = float.MaxValue;
        var yMax = float.MinValue;

        // Horizontal side faces in Chunk.CubeFaces order: -Z, +Z, -X, +X
        Span<int> sideFaces = [0, 1, 2, 3];
        const int topFace = 4;

        void AddQuad(List<Vertex> targetVertices, List<ushort> targetIndices,
            int faceIndex, Vector3 cellOrigin, ushort tile, Span<byte> shades, ReadOnlySpan<float> suns,
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
                var pos = cellOrigin + face[c] * cellSize;
                // The region AABB is what the frustum and the burial test use, so the sea
                // sheet has to widen it too
                if (pos.Y < yMin) yMin = pos.Y;
                if (pos.Y > yMax) yMax = pos.Y;
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
                    // (Chunk.EmitTintedCubeFace); bit 4 makes the fragment apply BiomeTint.Gain.
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
        void AddBlockFace(int face, Vector3 origin, BlockType block, Span<byte> faceShades,
            ReadOnlySpan<float> faceSuns, bool lodLeaves = false)
        {
            if (block.BiomeTint is { } bt)
            {
                if (bt.Base[face] != BiomeTintFaces.NoTile)
                    AddQuad(vertices, indices, face, origin, bt.Base[face], faceShades, faceSuns, lodLeaves: lodLeaves);
                if (bt.Tinted[face] != BiomeTintFaces.NoTile)
                    AddQuad(vertices, indices, face, origin, bt.Tinted[face], faceShades, faceSuns,
                        lodLeaves: lodLeaves, tinted: bt.Palette);
                return;
            }

            AddQuad(vertices, indices, face, origin, block.FaceTiles[face], faceShades, faceSuns, lodLeaves: lodLeaves);
        }

        // The water reads as one flat sheet at range, so it is a single +Y quad per wet cell at that
        // cell's own level minus this level's height bias (the terrain shells are lowered the same
        // way) and a further LodWaterSink, which keeps level 0 just under the real chunk water it
        // meets at the full-detail ring instead of z-fighting it. A lake carries its own level, so
        // each cell adds its surface rather than one sea plane.
        var water = BlockType.ById[TerrainGenerator.SeaBlock]!;
        var waterTile = water.FaceTiles[topFace];
        var sheetDrop = FluidState.SourceHeight - spec.HeightBias - WorldGenerationConfig.LodWaterSink;
        Span<byte> waterShades = [
            Chunk.ShadeLut[(topFace << 2) | 3], Chunk.ShadeLut[(topFace << 2) | 3],
            Chunk.ShadeLut[(topFace << 2) | 3], Chunk.ShadeLut[(topFace << 2) | 3]
        ];

        // Per-vertex ambient occlusion derived from the column-top grid (same formula and
        // shade table as chunk meshing): a neighboring column rising above a face corner
        // occludes it. Cell-scale AO, so the soft shadows match the LOD resolution.
        Span<byte> shades = stackalloc byte[4];
        // Per-vertex sky exposure from the same grid (LodSkyExposure); the sea/ice sheet is open sky
        Span<float> suns = stackalloc float[4];
        Span<float> waterSuns = [1f, 1f, 1f, 1f];

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
                    suns[c] = LodSkyExposure.TopFace(ground, top,
                        topCell[(gi + dx) * heightGrid + gj],
                        topCell[gi * heightGrid + (gj + dz)],
                        topCell[(gi + dx) * heightGrid + (gj + dz)]);
                }

                // Shoreline columns wear sand (gravel where the cell is patched) like the
                // chunks; beach-ness comes from the uneroded cell max, since the eroded ground
                // top can sit a whole band below the real shoreline
                var beach = beachTop[gi * heightGrid + gj];
                var cap = beach && gravelTop[gi * heightGrid + gj] ? gravel : sand;
                var soil = beach ? sand : dirt;
                var rock = rockTop[gi * heightGrid + gj];
                var snowCell = snowTop[gi * heightGrid + gj];
                var biome = biomeGrid[gi * heightGrid + gj];
                var cellLevel = waterLevels[gi * heightGrid + gj];
                var isSeaCell = cellLevel > TerrainGenerator.NoWater;
                var sheetY = isSeaCell ? cellLevel + sheetDrop : 0f;
                var floor = seaFloor[gi * heightGrid + gj];
                // The edited surface's own block (see the tile rule), which outranks the generated
                // material chain wherever it is the drawn surface.
                var edited = hasRegionOverrides && topBlockGrid[gi * heightGrid + gj] is var eb and not 0
                    ? BlockType.ById[eb]
                    : null;

                // The block the cell's drawn surface wears: the canopy bump's leaves above the
                // ground, else the edited block, else the generated material chain.
                var surface = edited
                    ?? (isSeaCell ? FloorBlock(floor)   // seabed wears its depth band, not dirt
                    : beach ? cap
                    : rock ? stone
                    : snowCell ? snow
                    : biome == Biome.Desert ? sand
                    : grass);
                AddBlockFace(topFace, new Vector3(i * cellSize, top * cellSize, j * cellSize),
                    top > ground ? CanopyBlock(canopyLeaf[gi * heightGrid + gj]) : surface,
                    shades, suns, lodLeaves: top > ground);

                // AddQuad places a +Y face one cell above its origin, so the origin sits a
                // cell below the sheet. A frozen cell shows snow-covered ice instead of open
                // water, and is not flagged water: the flag selects the fragment-shader water
                // model regardless of tile, so flagging ice would shade it as water.
                if (isSeaCell)
                {
                    var frozen = iceTop[gi * heightGrid + gj];
                    var snowCovered = snowTop[gi * heightGrid + gj];
                    AddQuad(waterVertices, waterIndices, topFace,
                        new Vector3(i * cellSize, sheetY - cellSize, j * cellSize),
                        frozen ? (snowCovered ? snow.FaceTiles[topFace] : ice.FaceTiles[topFace]) : waterTile,
                        waterShades, waterSuns, isWater: !frozen);
                }

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

                            // Sky exposure reads only columns in front of the face: both front
                            // diagonals and the one straight ahead, never the coplanar wall column
                            var oppositeDiagonal = normal.X != 0
                                ? topCell[ni * heightGrid + (nj + (corner.Z == 0 ? 1 : -1))]
                                : topCell[(ni + (corner.X == 0 ? 1 : -1)) * heightGrid + nj];
                            var vertexY = cy + (corner.Y == 0 ? 0 : 1);
                            suns[c] = LodSkyExposure.SideFace(vertexY, cy, ground, top,
                                lateralTop, oppositeDiagonal, neighborTop);
                        }

                        // Canopy airspace wears leaves; at and below the eroded surface the
                        // grass/dirt/stone rule applies, measured from the ground (not the bump),
                        // with the biome's own top material and soil band beneath it
                        var sideOrigin = new Vector3(i * cellSize, cy * cellSize, j * cellSize);
                        if (cy > ground)
                        {
                            AddBlockFace(f, sideOrigin, CanopyBlock(canopyLeaf[gi * heightGrid + gj]),
                                shades, suns, lodLeaves: true);
                        }
                        else if (cy == top)
                        {
                            AddBlockFace(f, sideOrigin, surface, shades, suns);
                        }
                        else
                        {
                            // The soil/stone band below the top cell never tints.
                            var tile = (ground - cy) * cellSize <= WorldGenerationConfig.NominalDirtDepth
                                ? (biome == Biome.Desert ? sandstone.FaceTiles[f]
                                    : isSeaCell || rock || biome == Biome.Frozen ? stone.FaceTiles[f]
                                    : soil.FaceTiles[f])
                                : stone.FaceTiles[f];
                            AddQuad(vertices, indices, f, sideOrigin, tile, shades, suns);
                        }
                    }
                }
            }
        }

        return new LodMeshData(level, rx, rz, vertices, indices, waterVertices, waterIndices,
            yMin, yMax, columnCyMin, columnCyMax);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(groundTop);
            ArrayPool<int>.Shared.Return(topCell);
            ArrayPool<int>.Shared.Return(waterLevels);
            ArrayPool<bool>.Shared.Return(beachTop);
            ArrayPool<bool>.Shared.Return(gravelTop);
            ArrayPool<bool>.Shared.Return(snowTop);
            ArrayPool<bool>.Shared.Return(iceTop);
            ArrayPool<bool>.Shared.Return(rockTop);
            ArrayPool<Biome>.Shared.Return(biomeGrid);
            ArrayPool<byte>.Shared.Return(seaFloor);
            ArrayPool<byte>.Shared.Return(canopyLeaf);
            ArrayPool<int>.Shared.Return(fine);
            ArrayPool<int>.Shared.Return(xMin);
            if (!ReferenceEquals(fineGen, fine)) ArrayPool<int>.Shared.Return(fineGen);
            ArrayPool<byte>.Shared.Return(fineBlock);
            ArrayPool<byte>.Shared.Return(topBlockGrid);
            ArrayPool<Vector3>.Shared.Return(grassTint);
            ArrayPool<Vector3>.Shared.Return(foliageTint);
        }
    }
}

/// <summary>
/// One finished region build on its way from the LodGen worker to the main thread.
///
/// The four lists are rented from <see cref="LodManager"/>'s <see cref="MeshBufferPool"/> by
/// <see cref="LodRegion.BuildMeshData"/> and are owned by whoever dequeues this record: the main
/// thread returns them exactly once, either after the GPU upload or when the build is dropped
/// out of range. Nothing may read them after that return, and the worker must never return them
/// itself — a completed build can sit in the upload queue for several frames.
/// </summary>
public record LodMeshData(int Level, int X, int Z, List<Vertex> Vertices, List<ushort> Indices,
    List<Vertex> WaterVertices, List<ushort> WaterIndices, float YMin, float YMax,
    int[] ColumnCyMin, int[] ColumnCyMax);
