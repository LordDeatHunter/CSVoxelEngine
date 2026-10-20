using System;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.Rendering.Geometry;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Lod;

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
    /// that list. Maintained by <see cref="LodRegionSet"/> on the main thread only.
    /// </summary>
    public int ListIndex = -1;

    /// <summary>
    /// This region's index in <c>LodRegionSet._pendingCover</c>, or -1 while it is not in that
    /// list. Maintained by <see cref="LodRegionSet"/> on the main thread only, exactly like
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
    /// blended, after everything opaque (see TerrainPasses.RenderWorldTranslucent).
    /// </summary>
    public GeometrySlice? WaterSlice { get; private set; }

    // The pool both slices came from, so Dispose can hand the space back without an unload
    // having to thread a pool reference in. One pool covers both meshes: the pair is born from
    // one LodMeshData and dies in the same Dispose, so their lifetimes never diverge.
    private ChunkGeometryPool? _pool;

    public bool HasMesh => OpaqueSlice != null;

    /// <summary>Whether any cell of this region is below sea level, i.e. it has a water sheet.</summary>
    public bool HasWaterMesh => WaterSlice != null;

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
    /// from its <see cref="ColumnData"/> instead: the biome and water level at the cell center,
    /// snow from a Taiga or Frozen biome and sea ice from a Frozen one, beaches from
    /// <see cref="TerrainGenerator.IsBeachHeight"/> in the Ocean biome, bare rock from a zero
    /// <see cref="ColumnData.DirtDepth"/> in the Forest biome, and the tint lattice from the
    /// stored corner climate. A supplied column's far LOD therefore shows no canopy bumps (its
    /// trees are in its blocks) and no gravel caps.
    /// </summary>
    public static LodMeshData BuildMeshData(int level, int rx, int rz, MeshBufferPool? buffers = null,
        bool separableMin = true, IWorldSource? source = null)
        => LodMeshBuilder.Build(level, rx, rz, buffers, separableMin, source);
}
