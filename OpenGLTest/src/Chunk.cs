using System;
using System.Buffers;
using System.Collections.Generic;
using Silk.NET.OpenGL;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static silkgltriangle.ChunkLighting;
using static silkgltriangle.ColumnCache;

namespace silkgltriangle;

/// <summary>
/// Represents a 16x16x16 chunk of blocks in the world.
/// </summary>
public class Chunk : IDisposable
{
    public const int Size = 16;
    public const int SizeShift = 4; // log2(Size), for shift/mask coordinate math
    public const int SizeMask = Size - 1;
    public const int BlockCount = Size * Size * Size;

    // Meshing reads a snapshot of this chunk plus a 1-block border from neighbors
    internal const int Padded = Size + 2;
    internal const int PaddedSq = Padded * Padded;
    private const int PaddedCount = Padded * PaddedSq;

    // Voxel light travels up to LightPropagationRadius blocks through air, so it is
    // computed over a wider volume centered on this chunk: a lamp just across the
    // border must still light this chunk's faces. The 3x3x3 neighbor chunks cover it
    // (LightBorder < Size). A padded-snapshot index (px,py,pz) maps to light index
    // (px+LightOffset, py+LightOffset, pz+LightOffset).
    internal const int LightBorder = WorldGenerationConfig.LightPropagationRadius;
    internal const int LightSize = Size + LightBorder * 2;
    internal const int LightSq = LightSize * LightSize;
    internal const int LightCount = LightSize * LightSq;
    internal const int LightOffset = LightBorder - 1;

    // Tile layout lives on TextureAtlas (set once when the atlas is stitched at startup);
    // meshing only reads it, so background mesh threads need no synchronization.

    // Flat block storage; index = (x << 8) | (y << 4) | z
    private readonly byte[] _blocks = new byte[BlockCount];

    // Per-block state byte, parallel to _blocks and lazily allocated: null means "all zero",
    // which is what every non-fluid cell and every generated sea cell reads as (see
    // FluidState). Written on the main thread only; mesh threads copy the field to a local and
    // treat null as zeros, the same tolerated race as _blocks (an edit's own remesh wins after).
    private volatile byte[]? _state;

    // The light the current mesh was baked with, one byte per cell packed sun << 4 | block in
    // Idx order (see ChunkMeshData.Light). Published on the main thread by UpdateMesh, read on
    // the main thread by entity draws (EntityLight), never mutated after publish and swapped
    // by reference, so a reader sees one whole snapshot. Null = no snapshot (not meshed yet,
    // or an all-air/fully-enclosed chunk whose build skipped the light flood).
    private volatile byte[]? _light;

    // Counts non-air cells, water included: its only consumer is the all-air mesh fast path,
    // and a chunk holding nothing but sea water still has a surface to draw.
    private int _solidCount;

    /// <summary>
    /// Test seam: the non-air cell count <see cref="GenerateBlockData"/> reports. It exists so
    /// the vectorised recount there can be pinned against an independent scalar oracle walking
    /// <see cref="GetLocalBlock"/>; nothing in the engine reads it.
    /// </summary>
    internal int SolidCount => _solidCount;

    private static readonly Vector3[] NeighborOffsets = Vector3Directions.CardinalDirections;
    public double lifetime = 0.0f;
    public const float MAX_LIFETIME = 0.5f;

    public Vector3 Position { get; }

    /// <summary>World-space corners of this chunk's cell, computed once (PERF_TODO R11).
    /// <see cref="Position"/> is get-only and set only in the constructor, so these never
    /// need invalidating. Four render passes frustum-test these every frame.</summary>
    public Vector3 WorldMin { get; }

    /// <inheritdoc cref="WorldMin"/>
    public Vector3 WorldMax { get; }

    /// <summary>
    /// Where this chunk's opaque geometry lives in the render manager's opaque
    /// <see cref="ChunkGeometryPool"/>, or <c>null</c> when there is nothing to draw.
    /// The chunk passes turn these into <c>glMultiDrawElementsIndirect</c> commands.
    /// </summary>
    public GeometrySlice? OpaqueSlice { get; private set; }

    /// <summary>
    /// Where this chunk's fluid and glass geometry lives in the render manager's translucent
    /// <see cref="ChunkGeometryPool"/>, or <c>null</c> when there is nothing to draw. Fluid
    /// surfaces live apart from the opaque geometry so RenderManager can draw them blended,
    /// after everything opaque (see RenderManager.RenderWorldTranslucent).
    /// </summary>
    public GeometrySlice? TranslucentSlice { get; private set; }

    // The pools the two slices came from, so Dispose can hand the space back without an
    // unload having to thread a pool reference in.
    private ChunkGeometryPool? _opaquePool;
    private ChunkGeometryPool? _translucentPool;

    /// <summary>
    /// Whether block data has been fully generated and is ready for meshing.
    /// </summary>
    public volatile bool IsBlockDataReady;

    /// <summary>
    /// Whether this chunk has an uploaded, non-empty mesh worth drawing.
    /// </summary>
    public bool HasMesh => OpaqueSlice != null;

    /// <summary>
    /// Whether this chunk has an uploaded fluid mesh for the blended pass. An ocean chunk
    /// holding nothing but water has one of these and no opaque mesh at all.
    /// </summary>
    public bool HasTranslucentMesh => TranslucentSlice != null;

    /// <summary>
    /// Whether this chunk's translucent mesh actually holds fluid. Glass shares that mesh, so
    /// <see cref="HasTranslucentMesh"/> cannot answer this, and the water-only work — the
    /// planar reflection and the scene colour/depth copies — keys on this instead.
    /// </summary>
    public bool HasWaterMesh { get; private set; }

    /// <summary>
    /// Whether a mesh has been applied at least once, even an empty one (all air, or solid with
    /// no exposed face). Main-thread only. The LOD uses it to tell "chunk is drawn" apart from
    /// "chunk has nothing to draw", which <see cref="HasMesh"/> cannot.
    /// </summary>
    public bool IsMeshed { get; private set; }

    /// <summary>
    /// This chunk's index in <see cref="ChunkManager.Renderable"/>, or -1 while it is not in
    /// that list (no mesh at all, or unloaded). Maintained by <see cref="ChunkManager"/> on
    /// the main thread only.
    /// </summary>
    public int RenderIndex = -1;

    /// <summary>
    /// This chunk's index in <see cref="ChunkManager.TranslucentRenderable"/>, or -1 while
    /// it is not in that list (no translucent mesh, or unloaded). Same contract as
    /// <see cref="RenderIndex"/>.
    /// </summary>
    public int TranslucentRenderIndex = -1;

    /// <summary>
    /// The lowest terrain-surface height (<see cref="ColumnData.MinHeight"/>: ravines already
    /// cut in, caves not — carvers only remove blocks) anywhere in this chunk's 16x16 column.
    /// Nothing in a chunk whose top is below this can be lit by the sun, which is what the
    /// shadow pass uses it for. Recorded while the block data is generated, where the column
    /// data is already in hand, so no per-frame cache lookup is needed;
    /// <c>int.MinValue</c> on a chunk built without block data (never rendered).
    /// </summary>
    public int SurfaceMinY { get; private set; } = int.MinValue;

    /// <summary>
    /// The fluid cells inside this chunk that could already move when it loads (local block
    /// indices as <see cref="Idx"/> produces them), or <c>null</c> when there are none.
    /// Generated sea water is static source blocks, so a cave carved into the sea floor leaves
    /// water standing against worldgen air that nothing would ever schedule; this is the list
    /// <see cref="FluidManager.WakeLoadedChunks"/> uses to make it pour in the moment the chunk
    /// loads. Generated water keeps the carved rule: open air above the terrain is not a cavity
    /// (see <see cref="FluidManager.IsWakeTarget"/>), so a ravine, beach or hillside never wakes
    /// the sea. A restored chunk can hold fluid the player poured, so a non-source (mid-flow)
    /// cell always counts, and so does a source above both its column's terrain top and water
    /// level (only ever poured) wherever it has a replaceable neighbour. Written once by the
    /// generation worker before <see cref="IsBlockDataReady"/> goes true and read once on the
    /// main thread afterwards, so it needs no locking.
    /// </summary>
    internal ushort[]? FluidFrontier { get; private set; }

    /// <summary>
    /// Whether a block edit has changed this chunk since it was generated, restored or last
    /// snapshotted (<see cref="TakeSnapshot"/>). Set by the three raw writers every edit goes
    /// through — <see cref="SetBlockRaw"/> (which <see cref="PlaceBlockAt"/>,
    /// <see cref="SetStateAt"/> and the fluid tick use), <see cref="RemoveBlockAt"/> and
    /// <see cref="ClearBlockAt"/> — and never by generation, so an untouched chunk is never
    /// written to disk and simply regenerates. Main thread only, like the edits.
    /// </summary>
    public bool ModifiedSinceSave { get; private set; }

    /// <summary>
    /// Whether this chunk's blocks came from a snapshot (<see cref="RestoreFrom"/>) — a save, or
    /// a world source that supplies chunks itself (<see cref="IWorldSource.Supplies"/>) — instead
    /// of worldgen. A restored chunk can hold fluid anywhere (poured water, or an imported map's),
    /// which is what makes the fluid wake scan its borders whatever height it sits at, and its
    /// <see cref="RestoredPlayerPlaced"/> may be null. Restoring never sets
    /// <see cref="ModifiedSinceSave"/>; only the raw writers do.
    /// </summary>
    public bool IsRestored { get; private set; }

    /// <summary>
    /// The player-placed cells (chunk-local <see cref="Idx"/> indices) a restored chunk brought
    /// back that the chunk manager has not yet adopted into its own player-placed set, or null.
    /// Set by <see cref="RestoreFrom"/> on the generation worker, cleared on the main thread
    /// once adopted.
    /// </summary>
    internal ushort[]? RestoredPlayerPlaced { get; set; }

    internal static readonly int[] FaceNeighborDelta = new int[6];
    internal static readonly int[][] AODeltas = new int[6][]; // 12 per face: (side1, side2, corner) x 4 corners
    internal const float SmoothLightDivisor = 60f; // 4 samples x level 15
    internal static readonly float[] FaceBrightness = [0.85f, 0.95f, 0.8f, 0.9f, 1.0f, 0.75f]; // matches CubeFaces order
    internal static readonly byte[] ShadeLut = new byte[6 * 4]; // [face * 4 + ao]

    static Chunk()
    {
        for (var i = 0; i < 6; i++)
        {
            FaceNeighborDelta[i] = PaddedDelta(CubeFaces[i].normal);

            var deltas = new int[12];
            for (var j = 0; j < 4; j++)
            for (var k = 0; k < 3; k++)
                deltas[j * 3 + k] = PaddedDelta(AONeighbors[i][j][k]);
            AODeltas[i] = deltas;

            for (var ao = 0; ao <= 3; ao++)
                ShadeLut[(i << 2) | ao] = (byte)(FaceBrightness[i] * ao * MathUtils.ONE_THIRD * 255);
        }
    }

    private static int PaddedDelta(Vector3 offset) =>
        (int)offset.X * PaddedSq + (int)offset.Y * Padded + (int)offset.Z;

    private static int LightDelta(Vector3 o) => (int)o.X * LightSq + (int)o.Y * LightSize + (int)o.Z;

    private static int FloorDiv(int a, int b) => MathUtils.FloorDiv(a, b);


    /// <summary>
    /// Copies the LightSize^3 block volume centered on this chunk straight from the 3x3x3
    /// neighboring chunks' block arrays, so the fill loop does zero dictionary lookups.
    /// <paramref name="neighbors"/> comes pre-resolved from <see cref="TryResolveNeighbors"/>,
    /// which guarantees the 6 face neighbors are present; the corner and edge slots may still
    /// be null and read as air, which only costs a little light accuracy in the diagonal
    /// corners of the wide light volume. Same thread-safety convention as FillPaddedSnapshot
    /// (block edits race the read; the edit's own remesh wins after).
    /// </summary>
    private void FillLightSnapshot(byte[] light, Chunk?[] neighbors)
    {
        // Per-axis map from volume coordinate to (chunk 0..2, local block 0..15). The volume
        // spans LightBorder cells into each neighbor: [1, LightBorder) of the previous
        // chunk, the full own chunk, then [0, LightBorder) of the next one.
        Span<int> mapChunk = stackalloc int[LightSize];
        Span<int> mapLocal = stackalloc int[LightSize];
        BuildLightAxisMap(mapChunk, mapLocal);

        for (var lx = 0; lx < LightSize; lx++)
        for (var ly = 0; ly < LightSize; ly++)
        {
            var rowBase = (lx * LightSize + ly) * LightSize;
            var neighborRow = (mapChunk[lx] * 3 + mapChunk[ly]) * 3;
            var blockRow = (mapLocal[lx] << (2 * SizeShift)) | (mapLocal[ly] << SizeShift);
            for (var lz = 0; lz < LightSize; lz++)
            {
                var neighbor = neighbors[neighborRow + mapChunk[lz]];
                light[rowBase + lz] = neighbor?._blocks[blockRow | mapLocal[lz]] ?? 0;
            }
        }
    }

    /// <summary>
    /// Flood-fills skylight and lamp light over the wide light snapshot (see
    /// FillLightSnapshot), so lamps and skylight changes up to LightPropagationRadius
    /// blocks away still light this chunk's faces. Sun seeds are air or leaf cells at or
    /// above the terrain height (nothing generated is ever solid above it, and canopies
    /// pass light); lamp seeds are emissive cells. Both spread through air and leaves
    /// losing one level per block, so caves and overhangs fall off into darkness. All
    /// inputs are local (snapshot + height cache), so this is thread-safe and needs no
    /// cross-chunk state. Seeds all enter at the same max level, so a FIFO queue visits
    /// every cell at its final level first, each cell is enqueued at most once, and the
    /// caller-sized queue never overflows.
    /// <paramref name="sun"/> and <paramref name="block"/> must be zeroed and hold at
    /// least LightCount entries each, as must <paramref name="queue"/>.
    /// <para>
    /// <paramref name="block"/> stores <c>level | slot &lt;&lt; 4</c>: the lamp level in bits 0-3
    /// (<see cref="BlockLevelMask"/>) and the <see cref="BlockLightPalette"/> slot of the light's
    /// colour in bits 4-6, so every read of a level must mask. <paramref name="sun"/> is plain
    /// levels. Returns whether any lamp seed carries a non-default slot; when none does, every
    /// slot is 0 and the byte is exactly the old level.
    /// </para>
    /// </summary>
    private bool ComputeLight(byte[] light, byte[] sun, byte[] block, int[] queue)
    {
        var worldY0 = LowerBoundY - LightBorder;

        var head = 0;
        var tail = 0;

        // The LightSize^2 volume columns only ever land in the 3x3 chunk columns around this one
        // (LightBorder < Size), so their heightmaps are fetched once by GenerateMeshData into
        // MeshScratch.Columns (ChunkLighting.FillColumns) instead of probing the column source per
        // column (PERF_TODO C2). Same nine columns, same heights - only the number of probes changes.
        Span<int> mapChunk = stackalloc int[LightSize];
        Span<int> mapLocal = stackalloc int[LightSize];
        BuildLightAxisMap(mapChunk, mapLocal);

        var columns = MeshScratch.Current.Columns;

        // Sunlight seeds: air cells at or above the terrain height see the sky directly.
        // Leaves seed too: canopies pass light, so the sky reaches straight through them
        // (branch tops under leaves stay lit; only depth falloff dims deep cover). Tree
        // wood seeds as well (see PassesLight): a thin limb never casts a full shadow.
        for (var lx = 0; lx < LightSize; lx++)
        {
            var columnRow = mapChunk[lx] * 3;
            var heightRow = mapLocal[lx] << SizeShift;
            for (var lz = 0; lz < LightSize; lz++)
            {
                var height = columns[columnRow + mapChunk[lz]].Heights[heightRow | mapLocal[lz]];
                var columnBase = (lx * LightSize) * LightSize + lz;
                for (var ly = 0; ly < LightSize; ly++)
                {
                    var i = columnBase + ly * LightSize;
                    if (!PassesLight(light[i])) continue;
                    if (worldY0 + ly >= height)
                    {
                        sun[i] = 15;
                        queue[tail++] = i;
                    }
                }
            }
        }

        SpreadLight(light, sun, queue, ref head, ref tail);

        // Lamp seeds: emissive cells glow at full strength, then spread like sunlight, each
        // carrying its colour slot in bits 4-6.
        head = 0;
        tail = 0;
        var anyTinted = false;
        var emissionById = BlockType.Emission;
        var slotById = BlockType.LightSlot;
        for (var i = 0; i < LightCount; i++)
        {
            var id = light[i];
            var emission = emissionById[id];
            if (emission == 0) continue;
            var slot = slotById[id];
            block[i] = (byte)(emission | slot << 4);
            anyTinted |= slot != 0;
            queue[tail++] = i;
        }

        // All-default light keeps the plain pass: the values (slot bits all 0) and the cost are
        // exactly what they were before coloured light existed
        if (anyTinted)
            SpreadBlockLight(light, block, queue, ref head, ref tail);
        else
            SpreadLight(light, block, queue, ref head, ref tail);
        return anyTinted;
    }

    /// <summary>Lamp level bits of a <c>blockLight</c> byte (see <see cref="ComputeLight"/>).</summary>
    internal const int BlockLevelMask = 15;



    /// <summary>Flat index of a chunk-local block; shared with the WorldGen carvers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Idx(int x, int y, int z) => (x << (2 * SizeShift)) | (y << SizeShift) | z;

    /// <summary>The inverse of <see cref="Idx"/>: the cell-local position a local block index names.</summary>
    internal static Vector3 LocalOfIdx(int idx) =>
        new(idx >> (2 * SizeShift), (idx >> SizeShift) & (Size - 1), idx & (Size - 1));

    /// <summary>
    /// Whether a fluid cell is a hot fluid open to the air above it — an ember and smoke spawn
    /// point. "Hot" is keyed off the block's <c>light.emission</c>, never its name, so any
    /// glowing fluid qualifies; the cell above must be neither fluid, solid nor occluding.
    /// </summary>
    internal static bool IsHotFluidVent(byte id, byte above) =>
        BlockType.IsFluid(id) && BlockType.Emission[id] > 0
        && !BlockType.IsFluid(above) && !BlockType.IsSolid(above) && !BlockType.Occludes(above);

    // gl is retained for signature stability only: every call site (and the headless tests)
    // still constructs a chunk with the context, but a chunk owns no GL object any more —
    // its geometry lives in the render manager's ChunkGeometryPools.
    public Chunk(GL gl, Vector3 position, bool generateBlockData = true)
    {
        _ = gl;
        Position = position;
        WorldMin = position * Size;
        WorldMax = WorldMin + new Vector3(Size);

        if (generateBlockData)
        {
            GenerateBlockData();
        }
    }

    /// <summary>
    /// Fills the terrain columns (grass / dirt / stone by depth), lets the cave carvers clear
    /// their air out of the result, floods everything below sea level, then stamps trees onto
    /// the surface. Runs on a generation worker thread.
    /// </summary>
    public void GenerateBlockData()
    {
        // Worldgen timing for the X/VOXEL_DEBUG console line (WorldgenStats, the `wgen` token)
        var genStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var column = ColumnCache.Get((int)Position.X, (int)Position.Z);
        var lowerBoundY = LowerBoundY;
        SurfaceMinY = column.MinHeight;

        // Entirely above the terrain: nothing to fill, nothing to carve, nothing to stamp.
        // With trees enabled the band above the surface still needs generation (a tree reaches
        // TreeGenerator.ReachUp above its base), and overhanging limbs near cliffs can cross
        // from higher neighbouring columns, so the test uses the tallest column in reach.
        var maxHeight = column.MaxHeight;
        if (WorldGenerationConfig.TreesEnabled)
        {
            var cx = (int)Position.X;
            var cz = (int)Position.Z;
            var reach = TreeGenerator.ReachChunkRadius;
            for (var dx = -reach; dx <= reach; dx++)
            for (var dz = -reach; dz <= reach; dz++)
            {
                var neighbourMax = ColumnCache.Get(cx + dx, cz + dz).MaxHeight;
                if (neighbourMax > maxHeight) maxHeight = neighbourMax;
            }
            maxHeight += TreeGenerator.ReachUp + 1;
        }

        // A chunk above the terrain still has water to fill when a body's surface reaches into it:
        // the sea reaches sea level, a lake its own level. A column that holds no water reports
        // NoWater, which no y is at or below.
        var needsWater = WorldGenerationConfig.WaterEnabled
                         && lowerBoundY <= column.MaxWaterLevel;
        if (lowerBoundY > maxHeight + 1 && !needsWater)
        {
            IsBlockDataReady = true;
            WorldgenStats.AddChunk(System.Diagnostics.Stopwatch.GetTimestamp() - genStart);
            return;
        }

        for (var x = 0; x < Size; x++)
        {
            for (var z = 0; z < Size; z++)
            {
                var i = (x << SizeShift) | z;
                ColumnCache.GenerateColumn(_blocks, (int)Position.X, (int)Position.Z, x, z, column.Heights[i], column.Bed[i], column.DirtDepth[i],
                    lowerBoundY, column.Biomes[i]);
            }
        }

        var worldX0 = (int)Position.X * Size;
        var worldZ0 = (int)Position.Z * Size;

        // Both carvers only ever clear blocks at or below their own column's terrain top:
        // NoiseCaves breaks each column at `depth < 0` before it reads a block or a sample, and
        // WormCarver skips every y at `depth < margin`, margin = WormSurfaceMargin * (1 - EntranceWeight)
        // in [0, 6] (EntranceWeight is a clamped SmoothStep). Both read this chunk's own
        // column.Heights, so a chunk whose tallest column ends below its floor is untouchable by
        // either and skipping them is bit-identical. It saves the 1000-call cave lattice
        // (NoiseCaves.SampleLattice) on every sea chunk the needsWater clause above keeps alive
        // over its own sea floor.
        var carvable = column.MaxHeight >= lowerBoundY;

        if (carvable && WorldGenerationConfig.NoiseCavesEnabled)
            NoiseCaves.Carve(_blocks, column, worldX0, lowerBoundY, worldZ0);

        if (carvable && WorldGenerationConfig.WormCavesEnabled)
            WormCarver.Carve(_blocks, column, worldX0, lowerBoundY, worldZ0);

        // After the carvers, so veins rim cave walls instead of being eaten by them.
        if (WorldGenerationConfig.MalachiteEnabled)
            MalachiteGenerator.Stamp(_blocks, worldX0, lowerBoundY, worldZ0);

        // Same pass, same reason; coal's two host rocks let it also fill desert sandstone.
        if (WorldGenerationConfig.CoalEnabled)
            CoalGenerator.Stamp(_blocks, worldX0, lowerBoundY, worldZ0);

        // After the carvers (so caves under the sea floor flood) and before the trees and
        // pebbles, which skip submerged columns rather than growing underwater
        var hasWater = false;
        if (WorldGenerationConfig.WaterEnabled)
        {
            hasWater = FillWater(column, lowerBoundY, worldX0, worldZ0);
            StampMud(column, worldX0, lowerBoundY, worldZ0);
        }

        // After the sea: flooded cells are water, not air, so only dry cave floors pond lava
        if (WorldGenerationConfig.LavaEnabled)
            LavaGenerator.Stamp(_blocks, column, worldX0, lowerBoundY, worldZ0);

        // Every species (cacti included) stamps here, wood and leaves with their state bytes.
        // Through a local: a ref to the volatile field is refused, and nothing else can see the
        // chunk until IsBlockDataReady flips below.
        var treeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (WorldGenerationConfig.TreesEnabled)
        {
            var state = _state;
            TreeGenerator.Stamp(_blocks, ref state, worldX0, lowerBoundY, worldZ0);
            _state = state;
        }
        WorldgenStats.AddTreeTicks(System.Diagnostics.Stopwatch.GetTimestamp() - treeStart);

        // Frozen-only ice spikes go down before the snow, so a spike keeps its base cell
        if (WorldGenerationConfig.SnowAndIceEnabled)
            IceSpikeGenerator.Stamp(_blocks, worldX0, lowerBoundY, worldZ0);

        // Last of the cold features: freezes cold surface water to ice, then lays snow on the dry
        // ground and the ice sheet that is left
        if (WorldGenerationConfig.SnowAndIceEnabled)
            SnowGenerator.Stamp(_blocks, column, worldX0, lowerBoundY, worldZ0);

        if (WorldGenerationConfig.PebblesEnabled)
            PebbleGenerator.Stamp(_blocks, column, worldX0, lowerBoundY, worldZ0);

        // Before the flowers and foliage, so their per-column scatter fills in around a clump
        // instead of eating into it
        if (WorldGenerationConfig.CottonEnabled)
            CottonGenerator.Stamp(_blocks, column, worldX0, lowerBoundY, worldZ0);

        if (WorldGenerationConfig.FlowersEnabled)
            FlowerGenerator.Stamp(_blocks, column, worldX0, lowerBoundY, worldZ0);

        if (WorldGenerationConfig.FoliageEnabled)
            FoliageGenerator.Stamp(_blocks, column, worldX0, lowerBoundY, worldZ0);

        // Carving removed an unknown number of blocks; a recount is cheaper than bookkeeping.
        // Counting AIR and subtracting beats counting non-air: BlockType.Air is
        // `public const byte Air = 0`, so this binds to the vectorised
        // MemoryExtensions.Count<byte> with no cast, where a "!= Air" predicate would
        // have to run one comparison per cell.
        _solidCount = BlockCount - _blocks.AsSpan().Count(BlockType.Air);

        // Only a chunk that actually got flooded can have a frontier to report, plus any
        // chunk low enough to hold lava pockets (the scan itself is fluid-generic)
        if (hasWater || (WorldGenerationConfig.LavaEnabled && lowerBoundY <= WorldGenerationConfig.LavaMaxY))
            ScanFluidFrontier(column, lowerBoundY);

        IsBlockDataReady = true;
        WorldgenStats.AddChunk(System.Diagnostics.Stopwatch.GetTimestamp() - genStart);
    }

    /// <summary>
    /// Collects the fluid cells that could already move when this chunk loads into
    /// <see cref="FluidFrontier"/>. The sea is generated as static sources, so without this a
    /// cave or an entrance that breached it would leave a wall of water standing beside worldgen
    /// air forever — nothing schedules those cells, since only block edits do.
    ///
    /// Generated water keeps the carved rule: only neighbours at or below their column's natural
    /// terrain top count (see <see cref="FluidManager.IsWakeTarget"/>), since that is what a
    /// carver opened, and open air above the terrain is deliberately excluded so a generated sea
    /// never wakes across a ravine, a beach or a hillside. Minecraft's generated water is static
    /// the same way. A restored chunk can also hold fluid the player poured: a non-source
    /// (mid-flow) cell is always reported, and a source above both its own column's terrain top
    /// and water level — which generated water and lava never are — wakes against any
    /// replaceable neighbour. Generated fluid is always a source at or below one of those two,
    /// so a generated chunk's frontier is exactly the carved rule's.
    ///
    /// +Y is deliberately not tested: every sea-surface cell has air above it and water never
    /// flows upward, so including it would report a useless 256 cells per surface chunk.
    /// Neighbours across a chunk border are left to the main-thread pass, which can wait until
    /// both chunks are loaded, so this only ever reads this chunk's own blocks and stays as
    /// thread-safe as the rest of worldgen.
    /// </summary>
    private void ScanFluidFrontier(ColumnData column, int lowerBoundY)
    {
        List<ushort>? frontier = null;

        for (var x = 0; x < Size; x++)
        for (var y = 0; y < Size; y++)
        for (var z = 0; z < Size; z++)
        {
            var i = Idx(x, y, z);
            if (!BlockType.IsFluid(_blocks[i])) continue;

            var fluidY = lowerBoundY + y;
            var column2D = (x << SizeShift) | z;
            var fluidHeight = column.Heights[column2D];
            var fluidWaterLevel = column.WaterLevel[column2D];

            var exposed = !FluidState.IsSource(GetLocalState(x, y, z))
                          || (x > 0 && IsWakeTarget(column, lowerBoundY, x - 1, y, z, fluidY, fluidHeight, fluidWaterLevel))
                          || (x < SizeMask && IsWakeTarget(column, lowerBoundY, x + 1, y, z, fluidY, fluidHeight, fluidWaterLevel))
                          || (z > 0 && IsWakeTarget(column, lowerBoundY, x, y, z - 1, fluidY, fluidHeight, fluidWaterLevel))
                          || (z < SizeMask && IsWakeTarget(column, lowerBoundY, x, y, z + 1, fluidY, fluidHeight, fluidWaterLevel))
                          || (y > 0 && IsWakeTarget(column, lowerBoundY, x, y - 1, z, fluidY, fluidHeight, fluidWaterLevel));
            if (!exposed) continue;

            frontier ??= [];
            frontier.Add((ushort)i);
        }

        FluidFrontier = frontier?.ToArray();
    }

    /// <summary>Whether a chunk-local neighbour is somewhere the fluid beside it may wake into
    /// (<see cref="FluidManager.IsWakeTarget"/> with this chunk's column data).</summary>
    private bool IsWakeTarget(ColumnData column, int lowerBoundY, int x, int y, int z,
        int fluidY, int fluidColumnHeight, int fluidColumnWaterLevel) =>
        FluidManager.IsWakeTarget(_blocks[Idx(x, y, z)], lowerBoundY + y,
            column.Heights[(x << SizeShift) | z], fluidY, fluidColumnHeight, fluidColumnWaterLevel);

    /// <summary>
    /// Floods every air cell up to each column's own water level (see
    /// <see cref="ColumnData.WaterLevel"/>) in a wet column — an ocean floor, or a lake or river bed
    /// — so oceans, lakes, rivers and sea-floor caves all fill while the dry land basins stay dry.
    /// A lake's level is its own surface, so the pond rests in the ground it was dug into instead of
    /// reaching sea level. Wetness reads the column's <see cref="ColumnData.Bed"/>: a ravine that
    /// merely cut a dry column below its level holds no water, while a ravine under the ocean just
    /// deepens it (the cut is faded out across every other water body, see
    /// <c>TerrainGenerator.RavineCutFade</c>). Generated water is always a source (state 0), which
    /// is what an untouched
    /// state byte reads as, so nothing is written to <c>_state</c> here. A pure per-column function
    /// of world position like the rest of worldgen: neighbours agree at their borders with no shared
    /// state. Returns whether any cell was flooded, which is what decides if this chunk can have a
    /// fluid frontier to report.
    /// </summary>
    private bool FillWater(ColumnData column, int lowerBoundY, int worldX0, int worldZ0)
    {
        var filled = false;

        for (var x = 0; x < Size; x++)
        for (var z = 0; z < Size; z++)
        {
            var local = (x << SizeShift) | z;
            var level = column.WaterLevel[local];
            if (column.Bed[local] >= level) continue;

            var top = Math.Min(level - lowerBoundY, Size - 1);
            if (top < 0) continue;

            for (var y = 0; y <= top; y++)
            {
                var i = Idx(x, y, z);
                if (_blocks[i] != BlockType.Air) continue;
                _blocks[i] = TerrainGenerator.SeaBlock;
                filled = true;
            }
        }

        return filled;
    }

    /// <summary>
    /// Rots dirt touching the fresh sea to mud on the spot, so worldgen ground spawns muddy
    /// instead of waiting for the fluid tick's watch to convert it. In-chunk neighbours read
    /// straight off this chunk's blocks; across a chunk border the sea is a pure function of
    /// the neighbour column's height, so that is tested analytically. A cave flood breaching
    /// across a border is left to the fluid tick, the same split <see cref="ScanFluidFrontier"/>
    /// uses: worldgen stays a pure per-chunk function with no cross-chunk block reads.
    /// </summary>
    private void StampMud(ColumnData column, int worldX0, int lowerBoundY, int worldZ0)
    {
        // A chunk starting above every level this column can hold water at can neither hold any
        // nor touch any within one cell
        if (lowerBoundY > Math.Max(column.MaxWaterLevel, WorldGenerationConfig.SeaLevel) + 1) return;

        var dirt = TerrainGenerator.SoilBlock;
        var water = TerrainGenerator.SeaBlock;
        var mud = TerrainGenerator.MudBlock;

        for (var x = 0; x < Size; x++)
        for (var y = 0; y < Size; y++)
        for (var z = 0; z < Size; z++)
        {
            if (_blocks[Idx(x, y, z)] != dirt) continue;
            if (TouchesSeaWater(x, y, z, worldX0, lowerBoundY, worldZ0, water))
                _blocks[Idx(x, y, z)] = mud;
        }
    }

    /// <summary>
    /// Whether any of the 26 cells around a chunk-local cell holds water: a direct block read
    /// inside this chunk, or the same <see cref="TerrainGenerator.IsWaterColumn"/> test
    /// <see cref="FillWater"/> is built on outside it. (It was a plain height test before lakes
    /// and rivers, when any below-waterline column held water; that is no longer true.)
    /// </summary>
    private bool TouchesSeaWater(int x, int y, int z, int worldX0, int lowerBoundY, int worldZ0, byte water)
    {
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;

            var nx = x + dx;
            var ny = y + dy;
            var nz = z + dz;
            if (nx >= 0 && ny >= 0 && nz >= 0 && nx < Size && ny < Size && nz < Size)
            {
                if (_blocks[Idx(nx, ny, nz)] == water) return true;
                continue;
            }

            var wx = worldX0 + x + dx;
            var wy = lowerBoundY + y + dy;
            var wz = worldZ0 + z + dz;
            var neighbour = ColumnCache.Get(wx >> SizeShift, wz >> SizeShift);
            var local = ((wx & SizeMask) << SizeShift) | (wz & SizeMask);
            if (wy > neighbour.Heights[local] && wy <= neighbour.WaterLevel[local])
                return true;
        }

        return false;
    }

    // Solid from the chunk floor up to the terrain height: grass on top, dirt for dirtDepth
    // blocks under it, stone below that. dirtDepth == 0 marks a bare stone column
    // (see ColumnData.DirtDepth) - deep ravine walls, where the top block is stone as well.
    // A wet column tops out in dirt instead: grass does not grow on a sea, lake or river bed.
    // Shoreline columns (see TerrainGenerator.IsBeachHeight) build from sand instead, capped
    // with gravel where TerrainGenerator.IsGravelPatch holds. `terrainHeight` (ravines cut in)
    // decides how deep to fill; `bed` (ravines not cut) decides whether the column is water.

    public int LowerBoundY => (int)Position.Y * Size;
    public int UpperBoundY => (int)Position.Y * Size + Size;

    /// <summary>
    /// Reads a block using chunk-local coordinates. Coordinates must be in [0, Size).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetLocalBlock(int x, int y, int z) => _blocks[Idx(x, y, z)];

    /// <summary>
    /// Reads a cell's state byte using chunk-local coordinates; 0 when no state array exists
    /// yet. Coordinates must be in [0, Size). See <see cref="FluidState"/> for the encoding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetLocalState(int x, int y, int z) => _state?[Idx(x, y, z)] ?? 0;

    /// <summary>Hands the light snapshot of the mesh just applied to entity queries (main thread).</summary>
    internal void PublishLight(byte[]? packed) => _light = packed;

    /// <summary>
    /// Sun and block light (0..15) of a chunk-local cell from the snapshot the current mesh
    /// was baked with. False, with both levels 0, when no snapshot has been published.
    /// </summary>
    public bool TryGetLocalLight(int x, int y, int z, out byte sun, out byte block) =>
        TryGetLocalLight(x, y, z, out sun, out block, out _);

    /// <summary>
    /// <see cref="TryGetLocalLight(int, int, int, out byte, out byte)"/> plus the block light's
    /// colour slot (<see cref="BlockLightPalette"/>). Only a chunk that had a tinted seed publishes
    /// the slot half (the snapshot is then twice as long), so every other chunk answers slot 0.
    /// </summary>
    public bool TryGetLocalLight(int x, int y, int z, out byte sun, out byte block, out byte slot)
    {
        // One read of the reference, so both halves come from the same published snapshot
        var light = _light;
        if (light == null)
        {
            sun = 0;
            block = 0;
            slot = 0;
            return false;
        }
        var i = Idx(x, y, z);
        var packed = light[i];
        sun = (byte)(packed >> 4);
        block = (byte)(packed & 0xF);
        slot = light.Length > BlockCount ? light[BlockCount + i] : (byte)0;
        return true;
    }

    /// <summary>
    /// Writes a block and its state with no remeshing, no player-placed bookkeeping and no
    /// scheduling, returning the previous id. The fluid simulation edits hundreds of cells per
    /// tick and batches the remeshes itself (<see cref="ChunkManager.MarkDirty"/>), so the
    /// per-edit work of <see cref="PlaceBlockAt"/> would hitch. Main thread only, like every
    /// other block edit. Allocates the state array on the first non-zero state.
    /// </summary>
    public byte SetBlockRaw(int x, int y, int z, byte id, byte state)
    {
        var i = Idx(x, y, z);
        var previous = _blocks[i];

        // _solidCount tracks non-air cells, so only an air <-> non-air transition moves it
        if (previous == 0 && id != 0) _solidCount++;
        else if (previous != 0 && id == 0) _solidCount--;

        _blocks[i] = id;

        if (state != 0)
        {
            var states = _state;
            if (states == null)
            {
                states = new byte[BlockCount];
                _state = states;
            }
            states[i] = state;
        }
        else if (_state != null)
        {
            _state[i] = 0;
        }

        MarkModified();
        if (CellWritten is { } written)
            written((int)Position.X * Size + x, (int)Position.Y * Size + y, (int)Position.Z * Size + z, id, state);
        return previous;
    }

    /// <summary>
    /// A server sets this to observe every raw cell write (an edit or a simulation step) in world
    /// coordinates, so it can broadcast the change to clients. Null on a client and in singleplayer;
    /// worldgen never fires it, because it writes the block array directly.
    /// </summary>
    public static Action<int, int, int, byte, byte>? CellWritten { get; set; }

    /// <summary>Flags the chunk as holding unsaved edits (see <see cref="ModifiedSinceSave"/>).
    /// The test keeps the hot fluid-tick path from rewriting a field it already set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkModified()
    {
        if (!ModifiedSinceSave) ModifiedSinceSave = true;
    }

    /// <summary>
    /// Copies this chunk's blocks and state into a <see cref="ChunkSnapshot"/> for the save
    /// writer and clears <see cref="ModifiedSinceSave"/>. Copies only — no palette, no Deflate:
    /// the encoding happens on the writer thread. Main thread only, since that is the only
    /// thread that edits blocks, so the copy is consistent.
    /// </summary>
    /// <param name="playerPlaced">The chunk's player-placed cells as local <see cref="Idx"/>
    /// indices (the chunk manager owns that set), or null.</param>
    internal ChunkSnapshot TakeSnapshot(ushort[]? playerPlaced)
    {
        // The snapshot's constructor copies both arrays itself
        var snapshot = new ChunkSnapshot(_blocks, _state, playerPlaced);
        ModifiedSinceSave = false;
        return snapshot;
    }

    /// <summary>
    /// A snapshot of this chunk's blocks and state for the network, without
    /// <see cref="TakeSnapshot"/>'s save-dirty side effect: a server hands chunks to clients
    /// without pretending it just saved them. Main thread, like every read of the arrays.
    /// </summary>
    public ChunkSnapshot CaptureForNetwork() => new(_blocks, _state, playerPlaced: null);

    /// <summary>
    /// Fills this chunk from a save instead of generating it: the blocks, the state bytes and
    /// the player-placed cells (handed to the chunk manager through
    /// <see cref="RestoredPlayerPlaced"/>) come from <paramref name="snapshot"/>, and everything
    /// <see cref="GenerateBlockData"/> derives on the way — the non-air count, the surface
    /// floor and the fluid frontier — is recomputed from them. Runs on a generation worker on a
    /// chunk built with <c>generateBlockData: false</c>, before any other thread can see it;
    /// <see cref="IsBlockDataReady"/> goes true last, which is what publishes the rest.
    /// </summary>
    internal void RestoreFrom(ChunkSnapshot snapshot) => RestoreFrom(snapshot, streamedColumns: false);

    /// <summary>
    /// <see cref="RestoreFrom(ChunkSnapshot)"/> with the block source chosen: a streamed
    /// multiplayer chunk (<paramref name="streamedColumns"/> true) carries no worldgen column, so
    /// its surface floor is derived from its own cells and no fluid frontier is scanned (the server
    /// owns fluids). A save restore without a world source at hand uses the column cache as
    /// before, for the fluid frontier (<see cref="RestoreFrom(ChunkSnapshot, ColumnData)"/>).
    /// </summary>
    internal void RestoreFrom(ChunkSnapshot snapshot, bool streamedColumns)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!streamedColumns)
        {
            RestoreFrom(snapshot, ColumnCache.Get((int)Position.X, (int)Position.Z));
            return;
        }

        CopySnapshot(snapshot);
        SurfaceMinY = LowestSolidY();
        FinishRestore(snapshot);
    }

    /// <summary>
    /// <see cref="RestoreFrom(ChunkSnapshot)"/> against a known column: the surface floor comes
    /// from <paramref name="column"/> and the fluid frontier is scanned against its heights and
    /// water levels. This is the save store's restore (with the world source's column) and the
    /// way a world source that supplies chunks fills one (<see cref="IWorldSource.Fill"/>).
    /// </summary>
    internal void RestoreFrom(ChunkSnapshot snapshot, ColumnData column)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(column);

        CopySnapshot(snapshot);
        SurfaceMinY = column.MinHeight;
        // A restored chunk can hold fluid anywhere, so it always scans (a chunk with no fluid
        // cell simply reports nothing)
        ScanFluidFrontier(column, LowerBoundY);
        FinishRestore(snapshot);
    }

    // The blocks, state bytes and non-air count of a restore.
    private void CopySnapshot(ChunkSnapshot snapshot)
    {
        Array.Copy(snapshot.Blocks, _blocks, BlockCount);
        _state = snapshot.State is { } state ? (byte[])state.Clone() : null;
        _solidCount = BlockCount - _blocks.AsSpan().Count(BlockType.Air);
    }

    // The tail every restore shares; IsBlockDataReady goes true last, which publishes the rest.
    private void FinishRestore(ChunkSnapshot snapshot)
    {
        IsRestored = true;
        RestoredPlayerPlaced = snapshot.PlayerPlaced is { } placed ? (ushort[])placed.Clone() : null;

        IsBlockDataReady = true;
    }

    // The world Y of this chunk's lowest non-air cell, or int.MinValue when it is all air.
    private int LowestSolidY()
    {
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        for (var z = 0; z < Size; z++)
            if (_blocks[Idx(x, y, z)] != 0)
                return LowerBoundY + y;
        return int.MinValue;
    }

    /// <summary>Clears a block. Returns false if the cell is outside this chunk or already air.</summary>
    public bool RemoveBlockAt(Vector3 worldPos, ChunkManager chunkManager)
    {
        var localX = (int)(worldPos.X - Position.X * Size);
        var localY = (int)(worldPos.Y - Position.Y * Size);
        var localZ = (int)(worldPos.Z - Position.Z * Size);

        if (!InBounds(localX, localY, localZ)) return false;
        if (_blocks[Idx(localX, localY, localZ)] == 0) return false;
        _blocks[Idx(localX, localY, localZ)] = 0;
        _solidCount--;
        ClearState(Idx(localX, localY, localZ));
        MarkModified();

        chunkManager.UnmarkPlayerPlaced((int)worldPos.X, (int)worldPos.Y, (int)worldPos.Z);
        chunkManager.ImmediateRemeshWithBorders(Position, worldPos);
        MarkNeighborsDirty(worldPos, chunkManager);
        CellWritten?.Invoke((int)worldPos.X, (int)worldPos.Y, (int)worldPos.Z, 0, 0);
        return true;
    }

    /// <summary>
    /// Fills a block. Returns false if the cell is outside this chunk or already occupied, so
    /// callers can avoid spending an item on a placement that never happened.
    /// <paramref name="state"/> rides along into the state byte (a freshly grounded rock pile
    /// starts at 1); 0 clears it, which is what every ordinary placement wants.
    /// </summary>
    public bool PlaceBlockAt(Vector3 worldPos, byte blockId, ChunkManager chunkManager, byte state = 0)
    {
        var localX = (int)(worldPos.X - Position.X * Size);
        var localY = (int)(worldPos.Y - Position.Y * Size);
        var localZ = (int)(worldPos.Z - Position.Z * Size);

        if (!InBounds(localX, localY, localZ)) return false;
        // Water is displaced by a placement instead of blocking it, and a stack a placement may
        // take over (layered snow) is overwritten too; anything else occupies the cell and the
        // placement fails, so the caller keeps its item
        var existing = _blocks[Idx(localX, localY, localZ)];
        if (!Placement.CanOccupy(existing, GetLocalState(localX, localY, localZ))) return false;

        // One write does id, count and state together: SetBlockRaw moves _solidCount only
        // on air <-> non-air transitions, so displacing water keeps the count.
        SetBlockRaw(localX, localY, localZ, blockId, state);

        chunkManager.MarkPlayerPlaced((int)worldPos.X, (int)worldPos.Y, (int)worldPos.Z);
        chunkManager.ImmediateRemeshWithBorders(Position, worldPos);
        MarkNeighborsDirty(worldPos, chunkManager);
        return true;
    }

    /// <summary>
    /// Rewrites one cell's state byte with no id change — a rock pile growing by one — and
    /// remeshes like a placement so the new stone shows at once. Returns false outside this
    /// chunk or on air.
    /// </summary>
    public bool SetStateAt(Vector3 worldPos, byte state, ChunkManager chunkManager)
    {
        var localX = (int)(worldPos.X - Position.X * Size);
        var localY = (int)(worldPos.Y - Position.Y * Size);
        var localZ = (int)(worldPos.Z - Position.Z * Size);

        if (!InBounds(localX, localY, localZ)) return false;
        var i = Idx(localX, localY, localZ);
        if (_blocks[i] == 0) return false;
        SetBlockRaw(localX, localY, localZ, _blocks[i], state);

        chunkManager.MarkPlayerPlaced((int)worldPos.X, (int)worldPos.Y, (int)worldPos.Z);
        chunkManager.ImmediateRemeshWithBorders(Position, worldPos);
        MarkNeighborsDirty(worldPos, chunkManager);
        return true;
    }

    /// <summary>
    /// Clears a block without remeshing and returns the removed id (0 if the cell was outside
    /// this chunk or already air). The caller must remesh every touched chunk afterwards —
    /// explosions clear hundreds of blocks, where per-block remeshing would hitch.
    /// Main thread only, like the other block edits.
    /// </summary>
    public byte ClearBlockAt(Vector3 worldPos)
    {
        var localX = (int)(worldPos.X - Position.X * Size);
        var localY = (int)(worldPos.Y - Position.Y * Size);
        var localZ = (int)(worldPos.Z - Position.Z * Size);

        if (!InBounds(localX, localY, localZ)) return 0;
        var removed = _blocks[Idx(localX, localY, localZ)];
        if (removed == 0) return 0;
        _blocks[Idx(localX, localY, localZ)] = 0;
        _solidCount--;
        ClearState(Idx(localX, localY, localZ));
        MarkModified();
        CellWritten?.Invoke((int)worldPos.X, (int)worldPos.Y, (int)worldPos.Z, 0, 0);
        return removed;
    }

    /// <summary>Zeroes one cell's state byte. Every write that is not a fluid write goes
    /// through here, so a stale level can never outlive the water that owned it.</summary>
    private void ClearState(int index)
    {
        if (_state != null) _state[index] = 0;
    }

    /// <summary>
    /// Schedules this chunk and everything in light radius for remeshing, so a batch edit
    /// that already cleared the blocks (see <see cref="ClearBlockAt"/>) becomes visible
    /// with correct light. Fully async through the dirty queue: a blast or a felling touches
    /// several chunks and building them all synchronously hitched the frame, while the dirty
    /// budget sorts closest-first and edited chunks sit at the camera, so they still rebuild
    /// within a frame or two. Single-block edits (<see cref="RemoveBlockAt"/>,
    /// <see cref="PlaceBlockAt"/>) stay synchronous: one build is cheap and instant.
    /// </summary>
    public void RemeshAfterBatchEdit(ChunkManager chunkManager)
    {
        var min = Position * Size;
        chunkManager.MarkAreaDirty(min, min + new Vector3(Size),
            WorldGenerationConfig.LightPropagationRadius);
    }

    /// <summary>
    /// Schedules every chunk in voxel-light radius of an edited block for remeshing, so
    /// lamp glow and skylight changes propagate their full 15 blocks instead of stopping
    /// at the edited chunk's border. The edited chunk itself was already remeshed
    /// synchronously by the caller and is skipped.
    /// </summary>
    private void MarkNeighborsDirty(Vector3 worldPos, ChunkManager chunkManager)
    {
        chunkManager.MarkAreaDirty(worldPos, worldPos + Vector3.One,
            WorldGenerationConfig.LightPropagationRadius, skip: Position);
    }

    private static bool InBounds(int x, int y, int z)
        => x >= 0 && y >= 0 && z >= 0 && x < Size && y < Size && z < Size;

    /// <summary>
    /// Builds this chunk's mesh, or returns null when a face neighbour, or one of the nine chunk
    /// columns' terrain (<see cref="ChunkManager.ColumnSource"/>, a streamed column that has not
    /// arrived), is missing.
    /// The whole 3x3x3 neighbourhood is resolved once, up front: the caller checked for face
    /// neighbours before scheduling, but this runs on a pool thread behind a semaphore and can
    /// start seconds later, by which time an unload may have taken one away. Meshing against
    /// that hole reads solid ground as air and bakes bogus side faces, sunk fluid corners and
    /// sunlight leaks along the border, which then stay until something re-dirties the chunk —
    /// so the build is abandoned instead and the caller puts the chunk back on the dirty list.
    /// </summary>
    public ChunkMeshData? GenerateMeshData(ChunkManager chunkManager) => GenerateMeshData(chunkManager, greedy: true);

    /// <summary>
    /// <see cref="GenerateMeshData(ChunkManager)"/> with the binary greedy pass switchable: the
    /// differential test meshes the same chunk both ways and compares. A parameter rather than
    /// a static flag because test classes run in parallel and pool threads read it.
    /// <paramref name="tintFields"/> overrides the biome tint field: a build whose chunk is not
    /// where its blocks really stand (TopplingTree.BuildMeshData's scratch chunk at (256, 0, 256))
    /// passes the field of the real footprint; null means the chunk's own fields, from the stored
    /// corner climate of its lattice columns (<see cref="BiomeTint.FieldsFromNeighbourhood"/>).
    /// </summary>
    internal ChunkMeshData? GenerateMeshData(ChunkManager chunkManager, bool greedy,
        BiomeTintFields? tintFields = null)
    {
        // Resolve before renting or touching anything: an abandoned build should cost nothing.
        // The neighbourhood lives in the thread's mesh scratch, not in two fresh arrays.
        var scratch = MeshScratch.Current;
        var neighbors = scratch.Neighbors;
        var neighborStates = scratch.NeighborStates;
        if (!TryResolveNeighbors(chunkManager, neighbors, neighborStates))
        {
            scratch.ClearNeighbors();
            return null;
        }

        // Nothing to draw: either all air, or a solid block of occluders whose six neighbours
        // seal every boundary face. The second case is most of the deep terrain in the load
        // sphere, and skipping it saves the light flood and the per-voxel walk below.
        if (_solidCount == 0 || IsFullyEnclosed(neighbors))
            return new ChunkMeshData([], [], [], [], false);

        // The nine chunk columns' terrain (heights, water levels, corner climate), resolved once
        // for the light flood, the water-surface codes and the tint. False is a streamed column
        // that has not arrived: the caller re-dirties exactly as for a vanished neighbour. Nothing
        // has been rented yet, so this exit needs no finally.
        if (!ChunkLighting.FillColumns(chunkManager.ColumnSource, (int)Position.X, (int)Position.Z, scratch.Columns))
        {
            scratch.ClearNeighbors();
            scratch.ClearColumns();
            return null;
        }

        var padded = ArrayPool<byte>.Shared.Rent(PaddedCount);
        var paddedState = ArrayPool<byte>.Shared.Rent(PaddedCount);
        // Rented arrays arrive dirty: the light snapshot is fully overwritten by the fill,
        // but the outputs must start at zero. Oversized rents are fine — everything below
        // only touches the first LightCount entries.
        var lightBlocks = ArrayPool<byte>.Shared.Rent(LightCount);
        var sunLight = ArrayPool<byte>.Shared.Rent(LightCount);
        var blockLight = ArrayPool<byte>.Shared.Rent(LightCount);
        var queue = ArrayPool<int>.Shared.Rent(LightCount);
        try
        {
            FillPaddedSnapshot(padded, paddedState, neighbors, neighborStates);
            FillLightSnapshot(lightBlocks, neighbors);
            scratch.ClearNeighbors();
            Array.Clear(sunLight, 0, LightCount);
            Array.Clear(blockLight, 0, LightCount);
            var anyTinted = ComputeLight(lightBlocks, sunLight, blockLight, queue);
            // Keep the chunk's own cells of the flood for entity lighting: a fresh array owned
            // by this build alone, published by UpdateMesh with the mesh it belongs to. A chunk
            // lit by coloured light appends a second half holding each cell's colour slot.
            var packed = new byte[anyTinted ? BlockCount * 2 : BlockCount];
            for (var x = 0; x < Size; x++)
            for (var y = 0; y < Size; y++)
            for (var z = 0; z < Size; z++)
            {
                var li = LightIdx(x, y, z);
                var b = blockLight[li];
                packed[Idx(x, y, z)] = (byte)(sunLight[li] << 4 | (b & BlockLevelMask));
                if (anyTinted) packed[BlockCount + Idx(x, y, z)] = (byte)(b >> 4);
            }
            return GenerateMeshDataFromSnapshot(padded, paddedState, sunLight, blockLight, chunkManager.MeshBuffers, greedy,
                    tintFields)
                with { Light = packed };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(padded);
            ArrayPool<byte>.Shared.Return(paddedState);
            ArrayPool<byte>.Shared.Return(lightBlocks);
            ArrayPool<byte>.Shared.Return(sunLight);
            ArrayPool<byte>.Shared.Return(blockLight);
            ArrayPool<int>.Shared.Return(queue);
            scratch.ClearColumns();
        }
    }

    // Slots of the six face-adjacent chunks in the 3x3x3 (cx*3+cy)*3+cz neighbourhood
    // (slot 13 is this chunk itself). Meshing cannot proceed without these; the twenty
    // edge and corner slots only feed light and fluid corner heights, so they may be null.
    private static readonly int[] FaceNeighborSlots = [4, 10, 12, 14, 16, 22];

    /// <summary>
    /// Resolves the 3x3x3 chunk neighbourhood once per mesh build, so both snapshot fills work
    /// from the same picture of the world instead of each racing the loader on its own. A
    /// neighbour that exists but has no block data yet counts as absent, exactly like one that
    /// was never loaded. Returns false when any of the six FACE neighbours is missing, which
    /// makes the mesh unbuildable (see <see cref="GenerateMeshData"/>); edge and corner
    /// neighbours are allowed to stay null and read as air.
    /// </summary>
    private bool TryResolveNeighbors(ChunkManager chunkManager, Chunk?[] neighbors, byte[]?[] neighborStates)
    {
        // Hoisted out of the 26 probes below, and FLOORED rather than truncated so this path
        // into the chunk key matches VoxelCoords.ChunkPosOf's convention on the negative side
        var px = MathUtils.FloorToInt(Position.X);
        var py = MathUtils.FloorToInt(Position.Y);
        var pz = MathUtils.FloorToInt(Position.Z);

        for (var cx = 0; cx < 3; cx++)
        for (var cy = 0; cy < 3; cy++)
        for (var cz = 0; cz < 3; cz++)
        {
            var ni = (cx * 3 + cy) * 3 + cz;
            if (ni == 13)
            {
                neighbors[ni] = this;
                neighborStates[ni] = _state;
                continue;
            }

            // Each neighbour's state array is read once into the array: the field is volatile
            // and may be allocated by a main-thread fluid edit while the snapshot is filled
            if (chunkManager.Chunks.TryGetValue(px + cx - 1, py + cy - 1, pz + cz - 1, out var neighbor)
                && neighbor.IsBlockDataReady)
            {
                neighbors[ni] = neighbor;
                neighborStates[ni] = neighbor._state;
            }
        }

        foreach (var slot in FaceNeighborSlots)
        {
            if (neighbors[slot] == null)
                return false;
        }

        return true;
    }

    /// <summary>
    /// True when every cell of this chunk occludes and the 16x16 plane of each face
    /// neighbour that touches this chunk occludes too, so no face of any block can be seen.
    /// An air pocket anywhere inside (<c>_solidCount &lt; BlockCount</c>) has real faces around
    /// it and fails the test. Reads neighbour block arrays directly, the same tolerated race as
    /// the snapshot fills: an edit's own remesh of both chunks wins afterwards.
    /// </summary>
    private bool IsFullyEnclosed(Chunk?[] neighbors)
    {
        if (_solidCount != BlockCount) return false;

        var occludes = BlockType.OccludesById;
        for (var i = 0; i < BlockCount; i++)
        {
            if (!occludes[_blocks[i]]) return false;
        }

        // Face neighbours (slot = (cx*3+cy)*3+cz): the plane that faces this chunk is the
        // neighbour's far plane along that axis.
        var negX = neighbors[4]!._blocks;
        var posX = neighbors[22]!._blocks;
        var negY = neighbors[10]!._blocks;
        var posY = neighbors[16]!._blocks;
        var negZ = neighbors[12]!._blocks;
        var posZ = neighbors[14]!._blocks;
        for (var a = 0; a < Size; a++)
        for (var b = 0; b < Size; b++)
        {
            if (!occludes[negX[Idx(Size - 1, a, b)]] || !occludes[posX[Idx(0, a, b)]]) return false;
            if (!occludes[negY[Idx(a, Size - 1, b)]] || !occludes[posY[Idx(a, 0, b)]]) return false;
            if (!occludes[negZ[Idx(a, b, Size - 1)]] || !occludes[posZ[Idx(a, b, 0)]]) return false;
        }

        return true;
    }

    /// <summary>
    /// Copies this chunk plus a 1-block border from the 26 surrounding chunks into a flat
    /// snapshot, so meshing does zero dictionary lookups. <paramref name="paddedState"/> is
    /// filled the same way with the per-block state bytes the fluid emitter needs (0 where the
    /// neighbour chunk is missing or has no state array yet). The arrays come pre-resolved from
    /// <see cref="TryResolveNeighbors"/>, so the 6 face neighbours are always present; a null
    /// edge or corner slot reads as air, which only affects fluid corner heights.
    /// </summary>
    private void FillPaddedSnapshot(byte[] padded, byte[] paddedState, Chunk?[] neighbors, byte[]?[] neighborStates)
    {
        for (var px = 0; px < Padded; px++)
        {
            var cx = px == 0 ? 0 : px == Padded - 1 ? 2 : 1;
            var lx = (px - 1) & SizeMask;

            for (var py = 0; py < Padded; py++)
            {
                var cy = py == 0 ? 0 : py == Padded - 1 ? 2 : 1;
                var ly = (py - 1) & SizeMask;
                var rowBase = px * PaddedSq + py * Padded;
                var neighborRow = (cx * 3 + cy) * 3;

                for (var pz = 0; pz < Padded; pz++)
                {
                    var cz = pz == 0 ? 0 : pz == Padded - 1 ? 2 : 1;
                    var ni = neighborRow + cz;
                    var neighbor = neighbors[ni];
                    var blockIndex = Idx(lx, ly, (pz - 1) & SizeMask);
                    padded[rowBase + pz] = neighbor?._blocks[blockIndex] ?? 0;
                    paddedState[rowBase + pz] = neighborStates[ni]?[blockIndex] ?? 0;
                }
            }
        }
    }

    /// <summary>
    /// Turns the padded block snapshot into vertices. Plain full cubes (<see cref="BlockType.IsGreedy"/>)
    /// go through the binary greedy pass first (<see cref="EmitGreedyFaces"/>), which merges
    /// identical neighbouring faces into one quad. The per-voxel loop then handles the rest: each
    /// block picks an emitter from its <see cref="BlockType.Renderer"/> — the procedural ones
    /// (tree wood, pebbles, branch piles) build their geometry from tiles, everything else
    /// replays its baked model's quads.
    /// </summary>
    private ChunkMeshData GenerateMeshDataFromSnapshot(byte[] padded, byte[] paddedState,
        byte[] sunLight, byte[] blockLight, MeshBufferPool pool, bool greedy, BiomeTintFields? tintFields)
    {
        // Rented, not allocated: ChunkManager returns them once the mesh is on the GPU
        var vertices = pool.RentVertices();
        var indices = pool.RentIndices();
        ushort indexOffset = 0;

        // Fluids build a second, blended mesh drawn after the opaque world, so whatever sits
        // behind the water shows through it. Rented like the opaque lists; chunks without
        // fluid or glass return them untouched (see MeshBufferPool.Return).
        var tVertices = pool.RentTranslucentVertices();
        var tIndices = pool.RentTranslucentIndices();
        ushort tIndexOffset = 0;

        // Glass shares the translucent lists, so "has a translucent mesh" is not "has water";
        // the reflection and scene-copy passes need the narrower answer.
        var hasWater = false;

        // Exposed hot-fluid cells (lava open to the air), allocated only when one turns up
        List<ushort>? vents = null;

        // World origin of this chunk, for the procedural emitters' per-cell world position; a
        // submerged face whose column leaves the padded snapshot reads its water level from the
        // build's resolved columns instead (SubmergedSurfaceCode)
        var chunkBaseY = LowerBoundY;
        var worldX0 = (int)Position.X * Size;
        var worldZ0 = (int)Position.Z * Size;
        var columns = MeshScratch.Current.Columns;

        // Biome tint of grass and leaves, both palettes from the stored corner climate of the
        // four lattice columns (no noise evaluation). A scratch build away from its blocks' real
        // position (a felled tree) passes the field of the real footprint instead.
        var fields = tintFields ?? BiomeTint.FieldsFromNeighbourhood(columns);
        // Corner scratch of a biome-tinted face in the per-voxel loop below
        Span<byte> tShade = stackalloc byte[4];
        Span<float> tSun = stackalloc float[4];
        Span<float> tBlock = stackalloc float[4];
        Span<ushort> tTint = stackalloc ushort[4];

        if (greedy)
            ChunkMesher.EmitGreedyFaces(MeshScratch.Current, padded, paddedState, chunkBaseY,
                sunLight, blockLight, vertices, indices, ref indexOffset, tVertices, tIndices, ref tIndexOffset, in fields);

        for (var x = 0; x < Size; x++)
        for (var y = 0; y < Size; y++)
        for (var z = 0; z < Size; z++)
        {
            var blockId = _blocks[Idx(x, y, z)];
            if (blockId == 0) continue;
            // Already emitted (merged) by the greedy pass above
            if (greedy && BlockType.IsGreedy(blockId)) continue;

            var type = BlockType.ById[blockId];
            if (type == null) continue;

            var basePos = new Vector3(x, y, z);
            var pIdx = (x + 1) * PaddedSq + (y + 1) * Padded + (z + 1);

            // Tree wood thinner than a full cube is a node box plus a tube toward every
            // connected neighbour (see EmitTreeWood); a full-thickness or detached cell falls
            // through to the plain model path below, which then also culls its faces against
            // a same-block full-thickness neighbour, since tree wood never occludes
            var cullSameFullWood = false;
            if (type.Renderer == BlockRenderer.TreeWood)
            {
                if (TreeWoodShape.IsThin(paddedState[pIdx]))
                {
                    ChunkMesher.EmitTreeWood(vertices, indices, ref indexOffset, padded, paddedState, pIdx, sunLight, blockLight,
                        x, y, z, type, basePos);
                    continue;
                }

                // A pixel-rim trunk shell with an exposed corner is bevelled from its four edge
                // neighbours (TreeWoodShape.RimCornerMask; the diagonals never change it, since a
                // cell only carves its own material); see EmitRimWood. Mask 0 is a plain cube.
                if (BlockType.HasPixelRim(blockId))
                {
                    var rimMask = ChunkMesher.RimMaskInSnapshot(padded, paddedState, pIdx, blockId);
                    if (rimMask != 0)
                    {
                        ChunkMesher.EmitRimWood(vertices, indices, ref indexOffset, padded, paddedState, pIdx, sunLight,
                            blockLight, x, y, z, type, basePos, rimMask);
                        continue;
                    }
                }

                cullSameFullWood = true;
            }

            // Pebbles render as 1-3 ankle-high boxes sharing the cell; see EmitPebbles
            if (type.Renderer == BlockRenderer.Pebbles)
            {
                ChunkMesher.EmitPebbles(worldX0, worldZ0, vertices, indices, ref indexOffset, padded, paddedState, pIdx, sunLight, blockLight,
                    x, y, z, type, basePos);
                continue;
            }

            // Branch piles render as 1-4 crossed sticks sharing the cell; see EmitBranchPile
            if (type.Renderer == BlockRenderer.BranchPile)
            {
                ChunkMesher.EmitBranchPile(worldX0, worldZ0, vertices, indices, ref indexOffset, padded, paddedState, pIdx, sunLight, blockLight,
                    x, y, z, type, basePos);
                continue;
            }

            // Layered snow renders as one box whose height is its stack depth; see EmitSnowLayers
            if (type.Renderer == BlockRenderer.SnowLayers)
            {
                ChunkMesher.EmitSnowLayers(worldX0, worldZ0, vertices, indices, ref indexOffset, padded, paddedState, pIdx, sunLight, blockLight,
                    x, y, z, type, basePos);
                continue;
            }

            // A fluid's sloped surface is read out of paddedState and goes into the
            // translucent lists; see EmitFluid
            if (type.Renderer == BlockRenderer.Fluid)
            {
                ChunkMesher.EmitFluid(tVertices, tIndices, ref tIndexOffset, padded, paddedState, pIdx,
                    sunLight, blockLight, x, y, z, type, basePos);
                hasWater = true;
                if (IsHotFluidVent(padded[pIdx], padded[pIdx + FaceNeighborDelta[4]]))
                    (vents ??= []).Add((ushort)Idx(x, y, z));
                continue;
            }

            // A multiblock part is a solid, occluding cell that draws nothing: the anchor's
            // model already covers the whole 2x2x2 volume it belongs to.
            if (type.Renderer == BlockRenderer.MultiblockPart) continue;

            // ...and the anchor draws that whole model, pre-rotated to the facing its state
            // byte's low two bits carry (see MultiblockShape), as does a facing block such as a
            // wall torch, turned about its own cell (see HorizontalFacing).
            var model = type.RotatedModels != null
                ? type.RotatedModels[paddedState[pIdx] & 3]
                : type.BakedModel;
            if (model == null) continue;

            // Canopy leaves and plants ride the wind in the vertex shader; everything else stays
            // planted. A rooted plant's per-vertex weight rides the alpha byte (ModelVertexAlpha).
            var sway = type.Sway ? 1f : 0f;
            var lightBase = LightIdx(x, y, z);
            var quads = model.Quads;

            // Translucent blocks (glass) replay the same baked quads into the blended
            // lists drawn after the opaque world; culling, AO and lighting below are shared.
            var dstVertices = type.Translucent ? tVertices : vertices;
            var dstIndices = type.Translucent ? tIndices : indices;
            ref var dstOffset = ref (type.Translucent ? ref tIndexOffset : ref indexOffset);

            for (var q = 0; q < quads.Length; q++)
            {
                var quad = quads[q];
                // The quern's plate and handle spin as a dynamic entity (see QuernManager):
                // the chunk keeps only the base quads below the model's air gap.
                if (type.Renderer == BlockRenderer.Quern && QuernRenderer.IsTopQuad(quad)) continue;
                var cullFace = quad.CullFace;
                int lightCell;
                // Bit 3 of the packed normal: this face looks into water, so the fragment
                // shader paints caustics on it. Interior sub-cell quads have no neighbour to
                // look into and stay false.
                var submerged = false;
                ushort waterSurface = 0;

                if (cullFace >= 0)
                {
                    var neighbour = padded[pIdx + FaceNeighborDelta[cullFace]];
                    // Only opaque neighbours hide a face. Leaves are cutout, so they never
                    // hide: a face culled behind one shows through its texture holes as an
                    // X-ray into the ground. Leaf-vs-leaf keeps both sides (Minecraft
                    // fancy-style) so the canopy still reads solid through the holes, while
                    // leaf-vs-rock draws the rock side only (the leaf side culls as before,
                    // so no coplanar double quad z-fights). CullSelf additionally hides a
                    // face against the same block, the way glass panes fuse.
                    if (BlockType.Occludes(neighbour) || (type.CullSelf && neighbour == blockId)) continue;
                    // Stacked full-thickness tree wood hides its shared faces itself - unless the
                    // neighbour is a bevelled pixel-rim shell whose face toward this cell is
                    // carved, which would open a hole (a core, a detached log or a mask-0 shell
                    // beside a rounded corner keeps its face). Non-rim species never read a mask.
                    if (cullSameFullWood && neighbour == blockId
                        && WoodState.IsFullCube(paddedState[pIdx + FaceNeighborDelta[cullFace]])
                        && (!BlockType.HasPixelRim(blockId)
                            || ChunkMesher.RimFaceFull(padded, paddedState, pIdx + FaceNeighborDelta[cullFace], blockId,
                                TreeWoodShape.Opposite(cullFace)))) continue;

                    // Light of the air cell in front of the face, from the wide light volume
                    // (so lamps across the chunk border still reach): the flat fallback for
                    // quads that do not smooth; a full face averages per corner below.
                    // A neighbour that would read as black light is exactly one that occludes,
                    // and that face is already gone, so no fallback is needed here.
                    lightCell = lightBase + LightFaceDelta[cullFace];
                    submerged = BlockType.IsFluid(neighbour);
                    // The water surface the caustics measure depth from: this face's own body
                    // of water, resolved exactly as the greedy pass keys it
                    if (submerged)
                        waterSurface = ChunkMesher.BakedWaterSurface(padded, paddedState, pIdx + FaceNeighborDelta[cullFace],
                            chunkBaseY, columns);
                }
                else
                {
                    // An interior quad of a sub-cell model has no neighbour to read: light it
                    // from its own cell, which passes light precisely because the model is not
                    // a full cube (the generalised form of the pebble trick).
                    lightCell = lightBase;
                }

                // Meshes are indexed with ushort (mirrors EmitFluid): stop emitting into a
                // list that is full instead of wrapping the offset
                if (dstVertices.Count > ushort.MaxValue - 3) continue;

                var sun = sunLight[lightCell] / 15f;
                var cellBlock = blockLight[lightCell];
                var block = (cellBlock & BlockLevelMask) / 15f;
                var flatTint = (ushort)LightTint.Flat(cellBlock & BlockLevelMask, cellBlock >> 4);
                var face = quad.Face;
                var positions = quad.Positions;
                var uvs = quad.Uvs;
                var normal = quad.Normal;
                // A sub-cell model block (a cross-shaped plant) can be climate-tinted on every
                // quad; a greedy block here is the per-voxel replay and reads type.BiomeTint
                // instead (see below), so the two never apply at once.
                var modelTint = type.ModelBiomeTint;

                if (!quad.Shade)
                {
                    // Minecraft's per-element "shade": false — full brightness, no face
                    // brightness and no ambient occlusion
                    for (var j = 0; j < 4; j++)
                    {
                        var vertex = new Vertex(basePos + positions[j], 255, 255, 255,
                            ChunkMesher.ModelVertexAlpha(type, positions[j].Y), uvs[j], quad.Tile,
                            sun, block, normal, sway, submerged, waterSurface, flatTint);
                        dstVertices.Add(modelTint is { } palette ? ChunkMesher.ApplyModelTint(vertex, fields.Field(palette)) : vertex);
                    }
                }
                else if (quad.IsFullFace)
                {
                    // Only a quad that fills its cube face has meaningful corner neighbours,
                    // so per-corner AO applies exactly as it did for hand-written cubes. A face
                    // that culls against its own side (exactly the set the greedy pass takes,
                    // see ContentRegistry.BakesGreedy) also averages its light per corner.
                    var aoDeltas = AODeltas[face];
                    var smooth = cullFace == face;

                    // A biome-tinted block BakesGreedy, so this is one of its six plain cube
                    // faces (smooth, positions and UVs in CubeFaces order): the corners are
                    // computed exactly as below and handed to the emitter the greedy pass uses.
                    if (type.BiomeTint is { } bt)
                    {
                        for (var j = 0; j < 4; j++)
                        {
                            var side1Id = padded[pIdx + aoDeltas[j * 3 + 0]];
                            var side2Id = padded[pIdx + aoDeltas[j * 3 + 1]];
                            var cornerId = padded[pIdx + aoDeltas[j * 3 + 2]];
                            var ao = VertexAO(BlockType.OccludesAO(side1Id), BlockType.OccludesAO(side2Id),
                                BlockType.OccludesAO(cornerId));
                            tShade[j] = ShadeLut[(face << 2) | ao];
                            var code = SmoothCornerLight(face, j, sunLight, blockLight, lightBase, side1Id, side2Id, cornerId);
                            tSun[j] = (code & 63) / SmoothLightDivisor;
                            tBlock[j] = ((code >> 6) & 63) / SmoothLightDivisor;
                            tTint[j] = (ushort)(code >> 12);
                        }
                        ChunkMesher.EmitTintedCubeFace(dstVertices, dstIndices, ref dstOffset, face, basePos, Vector3.One,
                            tShade, tSun, tBlock, tTint, bt, fields.Field(bt.Palette), submerged, waterSurface, sway);
                        continue;
                    }

                    for (var j = 0; j < 4; j++)
                    {
                        var side1Id = padded[pIdx + aoDeltas[j * 3 + 0]];
                        var side2Id = padded[pIdx + aoDeltas[j * 3 + 1]];
                        var cornerId = padded[pIdx + aoDeltas[j * 3 + 2]];

                        var ao = VertexAO(BlockType.OccludesAO(side1Id), BlockType.OccludesAO(side2Id),
                            BlockType.OccludesAO(cornerId));
                        var shade = ShadeLut[(face << 2) | ao];

                        var vSun = sun;
                        var vBlock = block;
                        var vTint = flatTint;
                        if (smooth)
                        {
                            var code = SmoothCornerLight(face, j, sunLight, blockLight, lightBase, side1Id, side2Id, cornerId);
                            vSun = (code & 63) / SmoothLightDivisor;
                            vBlock = ((code >> 6) & 63) / SmoothLightDivisor;
                            vTint = (ushort)(code >> 12);
                        }

                        var vertex = new Vertex(basePos + positions[j], shade, shade, shade,
                            ChunkMesher.ModelVertexAlpha(type, positions[j].Y), uvs[j], quad.Tile,
                            vSun, vBlock, normal, sway, submerged, waterSurface, vTint);
                        dstVertices.Add(modelTint is { } palette ? ChunkMesher.ApplyModelTint(vertex, fields.Field(palette)) : vertex);
                    }
                }
                else
                {
                    // A sub-cell quad floats inside the cell, where corner sampling would
                    // darken it by neighbours it does not touch: shade it flat and unoccluded
                    var shade = ShadeLut[(face << 2) | 3];
                    for (var j = 0; j < 4; j++)
                    {
                        var vertex = new Vertex(basePos + positions[j], shade, shade, shade,
                            ChunkMesher.ModelVertexAlpha(type, positions[j].Y), uvs[j], quad.Tile,
                            sun, block, normal, sway, submerged, waterSurface, flatTint);
                        dstVertices.Add(modelTint is { } palette ? ChunkMesher.ApplyModelTint(vertex, fields.Field(palette)) : vertex);
                    }
                }

                dstIndices.Add((ushort)(dstOffset + 0));
                dstIndices.Add((ushort)(dstOffset + 1));
                dstIndices.Add((ushort)(dstOffset + 2));
                dstIndices.Add((ushort)(dstOffset + 0));
                dstIndices.Add((ushort)(dstOffset + 2));
                dstIndices.Add((ushort)(dstOffset + 3));

                dstOffset += 4;
            }
        }

        return new ChunkMeshData(vertices, indices, tVertices, tIndices, hasWater)
            { HotFluidVents = vents?.ToArray() };
    }

    /// <summary>Corners of one axis-aligned face of the [min, max] box (cell-local).</summary>
    internal static void BoxFaceCorners(Vector3 min, Vector3 max, int face,
        out Vector3 a, out Vector3 b, out Vector3 c, out Vector3 d)
    {
        (a, b, c, d) = face switch
        {
            0 => (new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z),
                  new Vector3(max.X, max.Y, min.Z), new Vector3(max.X, min.Y, min.Z)),
            1 => (new Vector3(max.X, min.Y, max.Z), new Vector3(max.X, max.Y, max.Z),
                  new Vector3(min.X, max.Y, max.Z), new Vector3(min.X, min.Y, max.Z)),
            2 => (new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, max.Y, max.Z),
                  new Vector3(min.X, max.Y, min.Z), new Vector3(min.X, min.Y, min.Z)),
            3 => (new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, max.Y, min.Z),
                  new Vector3(max.X, max.Y, max.Z), new Vector3(max.X, min.Y, max.Z)),
            4 => (new Vector3(min.X, max.Y, min.Z), new Vector3(min.X, max.Y, max.Z),
                  new Vector3(max.X, max.Y, max.Z), new Vector3(max.X, max.Y, min.Z)),
            _ => (new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, min.Y, min.Z),
                  new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, min.Y, max.Z)),
        };
    }

    /// <summary>
    /// Tile-local UV of a point on an axis-aligned quad, in tile units. This is the one
    /// definition of how each face orients its texture: a full cube face maps its 0..1 cell to
    /// 0..1 on the tile, a sub-cell box (<see cref="CropUv"/>) samples the matching crop at the
    /// same texel density, and a quad merged over <paramref name="extent"/> cells runs 0..W and
    /// 0..H so the sampler's REPEAT tiles it. Per face, U runs along X or Z and V along Y (Z on
    /// top and bottom), mirrored where Minecraft's convention runs the other way — the same
    /// mirror <see cref="ModelBaker"/> applies, so baked cubes, procedural boxes and merged quads
    /// all sample identically and bark continues across log/branch joints at one scale.
    /// </summary>
    /// <param name="local">Point on the quad, relative to the quad's minimum corner.</param>
    /// <param name="extent">Size of the quad along each axis (1 on its plane axis).</param>
    internal static Vector2 TiledUv(int face, Vector3 local, Vector3 extent)
    {
        float u = face switch
        {
            0 => local.X,
            1 => extent.X - local.X,
            2 => extent.Z - local.Z,
            3 => local.Z,
            _ => local.X, // top and bottom run along X
        };
        float v = face switch
        {
            4 => extent.Z - local.Z, // top runs along Z
            5 => local.Z,            // bottom runs along Z
            _ => extent.Y - local.Y, // sides run bottom-to-top
        };
        return new Vector2(u, v);
    }

    /// <summary>Tile-local UV under a cell-local point of a sub-cell box (see <see cref="TiledUv"/>).</summary>
    internal static Vector2 CropUv(int face, Vector3 local) => TiledUv(face, local, Vector3.One);

    /// <summary>
    /// Local <see cref="Idx"/> indices of the exposed hot-fluid cells of the last applied mesh
    /// (ember/smoke spawn points), or null when there are none. Main thread only.
    /// </summary>
    public ushort[]? HotFluidVents { get; private set; }

    /// <summary>
    /// Uploads a finished mesh into the shared chunk geometry pools, releasing whatever this
    /// chunk held before. The upload is a synchronous <c>glBufferSubData</c>, so the caller may
    /// recycle <paramref name="meshData"/>'s lists the moment this returns. An empty list never
    /// touches a pool at all, which is what lets the headless tests pass <c>null</c> for both.
    /// Main thread only, like every other GL call.
    /// </summary>
    public void UpdateMesh(ChunkMeshData meshData, ChunkGeometryPool? opaquePool,
        ChunkGeometryPool? translucentPool)
    {
        IsMeshed = true;
        PublishLight(meshData.Light);
        ReleaseSlices();

        if (meshData.Vertices.Count > 0)
        {
            _opaquePool = opaquePool
                ?? throw new InvalidOperationException("non-empty chunk mesh without a geometry pool");
            OpaqueSlice = _opaquePool.Upload(CollectionsMarshal.AsSpan(meshData.Vertices),
                CollectionsMarshal.AsSpan(meshData.Indices));
        }

        if (meshData.TranslucentVertices.Count > 0)
        {
            _translucentPool = translucentPool
                ?? throw new InvalidOperationException("non-empty chunk mesh without a geometry pool");
            TranslucentSlice = _translucentPool.Upload(CollectionsMarshal.AsSpan(meshData.TranslucentVertices),
                CollectionsMarshal.AsSpan(meshData.TranslucentIndices));
        }

        HasWaterMesh = meshData.HasWater && meshData.TranslucentVertices.Count > 0;
        HotFluidVents = meshData.HotFluidVents;
    }

    /// <summary>
    /// Headless (no geometry pool) twin of <see cref="UpdateMesh"/>: marks the chunk meshed and
    /// publishes its light and fluid vents, but drops the vertex data instead of uploading it.
    /// Used only by a server world, whose chunks are never drawn, so the usual
    /// non-empty-mesh-without-a-pool throw never applies.
    /// </summary>
    public void MarkMeshedHeadless(ChunkMeshData meshData)
    {
        IsMeshed = true;
        PublishLight(meshData.Light);
        HasWaterMesh = meshData.HasWater && meshData.TranslucentVertices.Count > 0;
        HotFluidVents = meshData.HotFluidVents;
    }

    /// <summary>
    /// Hands both slices back to the pools they came from and forgets them. Freeing twice
    /// throws inside the allocator, so each slice is nulled the moment it is released.
    /// </summary>
    private void ReleaseSlices()
    {
        if (OpaqueSlice is { } opaque)
        {
            _opaquePool?.Free(opaque);
            OpaqueSlice = null;
        }
        _opaquePool = null;

        if (TranslucentSlice is { } translucent)
        {
            _translucentPool?.Free(translucent);
            TranslucentSlice = null;
        }
        _translucentPool = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int VertexAO(bool side1, bool side2, bool corner)
    {
        if (side1 && side2) return 0;
        return 3 - ((side1 ? 1 : 0) + (side2 ? 1 : 0) + (corner ? 1 : 0));
    }

    public void Dispose()
    {
        ReleaseSlices();
        HasWaterMesh = false;
        GC.SuppressFinalize(this);
    }

    internal static readonly (Vector3 normal, Vector3[] face)[] CubeFaces =
    [
       (new Vector3(0,  0, -1), [CubeVertexPositions.BackLeftBottom, CubeVertexPositions.BackLeftTop, CubeVertexPositions.BackRightTop, CubeVertexPositions.BackRightBottom]),
       (new Vector3(0,  0,  1), [CubeVertexPositions.FrontRightBottom, CubeVertexPositions.FrontRightTop, CubeVertexPositions.FrontLeftTop, CubeVertexPositions.FrontLeftBottom]),
       (new Vector3(-1, 0,  0), [CubeVertexPositions.FrontLeftBottom, CubeVertexPositions.FrontLeftTop, CubeVertexPositions.BackLeftTop, CubeVertexPositions.BackLeftBottom]),
       (new Vector3(1,  0,  0), [CubeVertexPositions.BackRightBottom, CubeVertexPositions.BackRightTop, CubeVertexPositions.FrontRightTop, CubeVertexPositions.FrontRightBottom]),
       (new Vector3(0,  1,  0), [CubeVertexPositions.BackLeftTop, CubeVertexPositions.FrontLeftTop, CubeVertexPositions.FrontRightTop, CubeVertexPositions.BackRightTop]),
       (new Vector3(0, -1,  0), [CubeVertexPositions.FrontLeftBottom, CubeVertexPositions.BackLeftBottom, CubeVertexPositions.BackRightBottom, CubeVertexPositions.FrontRightBottom])
    ];

    internal static readonly Vector3[][][] AONeighbors =
    [
        [
            [Vector3Directions.LeftBackward, Vector3Directions.DownBackward, Vector3Directions.DownLeftBackward],
            [Vector3Directions.LeftBackward, Vector3Directions.UpBackward, Vector3Directions.UpLeftBackward],
            [Vector3Directions.RightBackward, Vector3Directions.UpBackward, Vector3Directions.UpRightBackward],
            [Vector3Directions.RightBackward, Vector3Directions.DownBackward, Vector3Directions.DownRightBackward],
        ],
        [
            [Vector3Directions.RightForward, Vector3Directions.DownForward, Vector3Directions.DownRightForward],
            [Vector3Directions.RightForward, Vector3Directions.UpForward, Vector3Directions.UpRightForward],
            [Vector3Directions.LeftForward, Vector3Directions.UpForward, Vector3Directions.UpLeftForward],
            [Vector3Directions.LeftForward, Vector3Directions.DownForward, Vector3Directions.DownLeftForward],
        ],
        [
            [Vector3Directions.DownLeft, Vector3Directions.LeftForward, Vector3Directions.DownLeftForward],
            [Vector3Directions.UpLeft, Vector3Directions.LeftForward, Vector3Directions.UpLeftForward],
            [Vector3Directions.UpLeft, Vector3Directions.LeftBackward, Vector3Directions.UpLeftBackward],
            [Vector3Directions.DownLeft, Vector3Directions.LeftBackward, Vector3Directions.DownLeftBackward],
        ],
        [
            [Vector3Directions.DownRight, Vector3Directions.RightBackward, Vector3Directions.DownRightBackward],
            [Vector3Directions.UpRight, Vector3Directions.RightBackward, Vector3Directions.UpRightBackward],
            [Vector3Directions.UpRight, Vector3Directions.RightForward, Vector3Directions.UpRightForward],
            [Vector3Directions.DownRight, Vector3Directions.RightForward, Vector3Directions.DownRightForward],
        ],
        [
            [Vector3Directions.UpLeft, Vector3Directions.UpBackward, Vector3Directions.UpLeftBackward],
            [Vector3Directions.UpLeft, Vector3Directions.UpForward, Vector3Directions.UpLeftForward],
            [Vector3Directions.UpRight, Vector3Directions.UpForward, Vector3Directions.UpRightForward],
            [Vector3Directions.UpRight, Vector3Directions.UpBackward, Vector3Directions.UpRightBackward],
        ],
        [
            [Vector3Directions.DownLeft, Vector3Directions.DownForward, Vector3Directions.DownLeftForward],
            [Vector3Directions.DownLeft, Vector3Directions.DownBackward, Vector3Directions.DownLeftBackward],
            [Vector3Directions.DownRight, Vector3Directions.DownBackward, Vector3Directions.DownRightBackward],
            [Vector3Directions.DownRight, Vector3Directions.DownForward, Vector3Directions.DownRightForward],
        ]
    ];
}

/// <summary>
/// CPU-side mesh of one chunk, produced off the main thread. The opaque lists become the
/// chunk's normal mesh; the translucent ones its fluid mesh, drawn blended after everything
/// opaque so the sea floor shows through the water. <paramref name="HasWater"/> says whether
/// any of those translucent quads are fluid — glass lands in the same lists.
/// </summary>
public record ChunkMeshData(List<Vertex> Vertices, List<ushort> Indices,
    List<Vertex> TranslucentVertices, List<ushort> TranslucentIndices, bool HasWater)
{
    /// <summary>
    /// The chunk's own 16^3 cells of the light flood this mesh was built with, packed
    /// <c>sun &lt;&lt; 4 | block</c> (levels 0..15) in <see cref="Chunk.Idx"/> order, or null
    /// when the build skipped the light flood (all-air or fully enclosed).
    /// </summary>
    public byte[]? Light { get; init; }

    /// <summary>
    /// Local <see cref="Chunk.Idx"/> indices of the chunk's exposed hot-fluid cells
    /// (<see cref="Chunk.IsHotFluidVent"/>), or null when there are none.
    /// </summary>
    public ushort[]? HotFluidVents { get; init; }
}
