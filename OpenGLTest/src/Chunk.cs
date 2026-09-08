using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Silk.NET.OpenGL;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace silkgltriangle;

/// <summary>
/// Represents a 16x16x16 chunk of blocks in the world.
/// </summary>
public class Chunk : IDisposable
{
    public const int Size = 16;
    public const int SizeShift = 4; // log2(Size), for shift/mask coordinate math
    public const int SizeMask = Size - 1;
    private const int BlockCount = Size * Size * Size;

    // Meshing reads a snapshot of this chunk plus a 1-block border from neighbors
    private const int Padded = Size + 2;
    private const int PaddedSq = Padded * Padded;
    private const int PaddedCount = Padded * PaddedSq;

    // Voxel light travels up to LightPropagationRadius blocks through air, so it is
    // computed over a wider volume centered on this chunk: a lamp just across the
    // border must still light this chunk's faces. The 3x3x3 neighbor chunks cover it
    // (LightBorder < Size). A padded-snapshot index (px,py,pz) maps to light index
    // (px+LightOffset, py+LightOffset, pz+LightOffset).
    private const int LightBorder = WorldGenerationConfig.LightPropagationRadius;
    private const int LightSize = Size + LightBorder * 2;
    private const int LightSq = LightSize * LightSize;
    private const int LightCount = LightSize * LightSq;
    private const int LightOffset = LightBorder - 1;

    // Tile layout lives on TextureAtlas (set once when the atlas is stitched at startup);
    // meshing only reads it, so background mesh threads need no synchronization.

    // Flat block storage; index = (x << 8) | (y << 4) | z
    private readonly byte[] _blocks = new byte[BlockCount];

    // Per-block state byte, parallel to _blocks and lazily allocated: null means "all zero",
    // which is what every non-fluid cell and every generated sea cell reads as (see
    // FluidState). Written on the main thread only; mesh threads copy the field to a local and
    // treat null as zeros, the same tolerated race as _blocks (an edit's own remesh wins after).
    private volatile byte[]? _state;

    // Counts non-air cells, water included: its only consumer is the all-air mesh fast path,
    // and a chunk holding nothing but sea water still has a surface to draw.
    private int _solidCount;

    private static readonly Vector3[] NeighborOffsets = Vector3Directions.CardinalDirections;
    public double lifetime = 0.0f;
    public const float MAX_LIFETIME = 0.5f;

    public Vector3 Position { get; }
    private Mesh? _mesh;

    // Fluid surfaces live in their own mesh so RenderManager can draw them blended, after
    // everything opaque (see RenderManager.RenderWorldTranslucent)
    private Mesh? _translucentMesh;
    private readonly GL _gl;

    /// <summary>
    /// Whether block data has been fully generated and is ready for meshing.
    /// </summary>
    public volatile bool IsBlockDataReady;

    /// <summary>
    /// Whether this chunk has an uploaded, non-empty mesh worth drawing.
    /// </summary>
    public bool HasMesh => _mesh != null;

    /// <summary>
    /// Whether this chunk has an uploaded fluid mesh for the blended pass. An ocean chunk
    /// holding nothing but water has one of these and no opaque mesh at all.
    /// </summary>
    public bool HasTranslucentMesh => _translucentMesh != null;

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
    /// The lowest terrain-surface height (<see cref="ColumnData.MinHeight"/>: ravines already
    /// cut in, caves not — carvers only remove blocks) anywhere in this chunk's 16x16 column.
    /// Nothing in a chunk whose top is below this can be lit by the sun, which is what the
    /// shadow pass uses it for. Recorded while the block data is generated, where the column
    /// data is already in hand, so no per-frame cache lookup is needed;
    /// <c>int.MinValue</c> on a chunk built without block data (never rendered).
    /// </summary>
    public int SurfaceMinY { get; private set; } = int.MinValue;

    /// <summary>
    /// The fluid cells inside this chunk that already sit against somewhere they could flow
    /// (local block indices as <see cref="Idx"/> produces them), or <c>null</c> when there are
    /// none. Generated sea water is static source blocks, so a cave carved into the sea floor
    /// leaves water standing against worldgen air that nothing would ever schedule; this is the
    /// list <see cref="FluidManager.WakeLoadedChunks"/> uses to make it pour in the moment the
    /// chunk loads. Written once by the generation worker before <see cref="IsBlockDataReady"/>
    /// goes true and read once on the main thread afterwards, so it needs no locking.
    /// </summary>
    internal ushort[]? FluidFrontier { get; private set; }

    // Column data only depends on (x, z), so vertical chunk stacks share one entry.
    // The outermost LOD ring touches more columns than this (they're computed once per region
    // build and rarely revisited), so occasional Clear + recompute is cheaper than a bigger
    // cache. 16k entries is ~40 MB.
    private static readonly ConcurrentDictionary<(int X, int Z), ColumnData> ColumnCache = new();
    private const int ColumnCacheLimit = 16384;

    // Meshing lookup tables, flattened from CubeFaces/AONeighbors into padded-snapshot index deltas
    private static readonly int[] FaceNeighborDelta = new int[6];
    // Same face normals as light-volume index deltas, for the air cell in front of a face
    private static readonly int[] LightFaceDelta = new int[6];
    private static readonly int[][] AODeltas = new int[6][]; // 12 per face: (side1, side2, corner) x 4 corners
    internal static readonly float[] FaceBrightness = [0.85f, 0.95f, 0.8f, 0.9f, 1.0f, 0.75f]; // matches CubeFaces order
    internal static readonly byte[] ShadeLut = new byte[6 * 4]; // [face * 4 + ao]

    static Chunk()
    {
        for (var i = 0; i < 6; i++)
        {
            FaceNeighborDelta[i] = PaddedDelta(CubeFaces[i].normal);
            var n = CubeFaces[i].normal;
            LightFaceDelta[i] = (int)n.X * LightSq + (int)n.Y * LightSize + (int)n.Z;

            var deltas = new int[12];
            for (var j = 0; j < 4; j++)
            {
                deltas[j * 3 + 0] = PaddedDelta(AONeighbors[i][j][0]);
                deltas[j * 3 + 1] = PaddedDelta(AONeighbors[i][j][1]);
                deltas[j * 3 + 2] = PaddedDelta(AONeighbors[i][j][2]);
            }
            AODeltas[i] = deltas;

            for (var ao = 0; ao <= 3; ao++)
                ShadeLut[(i << 2) | ao] = (byte)(FaceBrightness[i] * ao * MathUtils.ONE_THIRD * 255);
        }
    }

    private static int PaddedDelta(Vector3 offset) =>
        (int)offset.X * PaddedSq + (int)offset.Y * Padded + (int)offset.Z;

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    /// <summary>Terrain height at a world (x, z) column, via the cached heightmaps.</summary>
    private static int ColumnHeight(int worldX, int worldZ)
    {
        var cx = FloorDiv(worldX, Size);
        var cz = FloorDiv(worldZ, Size);
        var lx = worldX - cx * Size;
        var lz = worldZ - cz * Size;
        return GetColumnHeights(cx, cz)[(lx << SizeShift) | lz];
    }

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
        for (var l = 0; l < LightSize; l++)
        {
            if (l < LightBorder) { mapChunk[l] = 0; mapLocal[l] = l + 1; }
            else if (l < LightBorder + Size) { mapChunk[l] = 1; mapLocal[l] = l - LightBorder; }
            else { mapChunk[l] = 2; mapLocal[l] = l - LightBorder - Size; }
        }

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
    /// </summary>
    private void ComputeLight(byte[] light, byte[] sun, byte[] block, int[] queue)
    {
        var worldX0 = (int)Position.X * Size - LightBorder;
        var worldY0 = LowerBoundY - LightBorder;
        var worldZ0 = (int)Position.Z * Size - LightBorder;

        var head = 0;
        var tail = 0;

        // Sunlight seeds: air cells at or above the terrain height see the sky directly.
        // Leaves seed too: canopies pass light, so the sky reaches straight through them
        // (branch tops under leaves stay lit; only depth falloff dims deep cover). Tapered
        // wood boxes seed as well (see PassesLight): a thin limb never casts a full shadow.
        for (var lx = 0; lx < LightSize; lx++)
        {
            var worldX = worldX0 + lx;
            for (var lz = 0; lz < LightSize; lz++)
            {
                var height = ColumnHeight(worldX, worldZ0 + lz);
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

        // Lamp seeds: emissive cells glow at full strength, then spread like sunlight.
        head = 0;
        tail = 0;
        for (var i = 0; i < LightCount; i++)
        {
            var emission = BlockType.Emission[light[i]];
            if (emission == 0) continue;
            block[i] = emission;
            queue[tail++] = i;
        }

        SpreadLight(light, block, queue, ref head, ref tail);
    }

    /// <summary>Whether voxel light travels through a block: air, plus leaves, whose canopy
    /// stays lit through instead of casting solid shade, plus the tapered wood boxes, which
    /// are far thinner than a full cube and should dapple rather than block, plus the
    /// ankle-high pebbles, which are too small to shade. Everything else
    /// (rock, full-cube logs, TNT) still blocks. AO keeps treating leaves as solid; geometry
    /// culling does not (see <see cref="BlockType.Occludes"/>). Which blocks pass is data now:
    /// <c>light.passes</c> in each block's JSON, defaulting to "not a full cube".</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool PassesLight(byte blockId) => BlockType.PassesLightById[blockId];

    /// <summary>One BFS pass: spreads <paramref name="levels"/> through light-passing cells
    /// (see <see cref="PassesLight"/>) of the light snapshot, losing one level per block.
    /// Shared by the sun and lamp passes. Every cell is still enqueued at most once, since
    /// every traversal costs exactly one level.</summary>
    private static void SpreadLight(byte[] light, byte[] levels, int[] queue, ref int head, ref int tail)
    {
        while (head < tail)
        {
            var c = queue[head++];
            var level = levels[c];
            if (level <= 1) continue;
            var next = (byte)(level - 1);

            var lx = c / LightSq;
            var rem = c - lx * LightSq;
            var ly = rem / LightSize;
            var lz = rem - ly * LightSize;

            if (lx > 0 && PassesLight(light[c - LightSq]) && levels[c - LightSq] < next)
                { levels[c - LightSq] = next; queue[tail++] = c - LightSq; }
            if (lx < LightSize - 1 && PassesLight(light[c + LightSq]) && levels[c + LightSq] < next)
                { levels[c + LightSq] = next; queue[tail++] = c + LightSq; }
            if (ly > 0 && PassesLight(light[c - LightSize]) && levels[c - LightSize] < next)
                { levels[c - LightSize] = next; queue[tail++] = c - LightSize; }
            if (ly < LightSize - 1 && PassesLight(light[c + LightSize]) && levels[c + LightSize] < next)
                { levels[c + LightSize] = next; queue[tail++] = c + LightSize; }
            if (lz > 0 && PassesLight(light[c - 1]) && levels[c - 1] < next)
                { levels[c - 1] = next; queue[tail++] = c - 1; }
            if (lz < LightSize - 1 && PassesLight(light[c + 1]) && levels[c + 1] < next)
                { levels[c + 1] = next; queue[tail++] = c + 1; }
        }
    }

    /// <summary>Light-volume index of a chunk-local block.</summary>
    private static int LightIdx(int x, int y, int z) =>
        ((x + LightBorder) * LightSize + (y + LightBorder)) * LightSize + (z + LightBorder);

    /// <summary>Flat index of a chunk-local block; shared with the WorldGen carvers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Idx(int x, int y, int z) => (x << (2 * SizeShift)) | (y << SizeShift) | z;

    public Chunk(GL gl, Vector3 position, bool generateBlockData = true)
    {
        _gl = gl;
        Position = position;

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
        var column = GetColumnData((int)Position.X, (int)Position.Z);
        var lowerBoundY = LowerBoundY;
        SurfaceMinY = column.MinHeight;

        // Entirely above the terrain: nothing to fill, nothing to carve, nothing to stamp.
        // With trees enabled the band above the surface still needs generation (crowns and
        // limbs reach TreeMaxHeightBlocks past it), and overhanging limbs near cliffs can
        // cross from higher neighbouring columns, so the test uses the tallest neighbour.
        var maxHeight = column.MaxHeight;
        if (WorldGenerationConfig.TreesEnabled)
        {
            var cx = (int)Position.X;
            var cz = (int)Position.Z;
            for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                var neighbourMax = GetColumnData(cx + dx, cz + dz).MaxHeight;
                if (neighbourMax > maxHeight) maxHeight = neighbourMax;
            }
            maxHeight += WorldGenerationConfig.TreeMaxHeightBlocks;
        }

        // A chunk above the terrain still has water to fill when it holds part of the sea:
        // it must reach sea level or below, and some column under it must be flooded
        var needsWater = WorldGenerationConfig.WaterEnabled
                         && lowerBoundY <= WorldGenerationConfig.SeaLevel
                         && column.MinHeight < WorldGenerationConfig.SeaLevel;
        if (lowerBoundY > maxHeight + 1 && !needsWater)
        {
            IsBlockDataReady = true;
            return;
        }

        for (var x = 0; x < Size; x++)
        {
            for (var z = 0; z < Size; z++)
            {
                var i = (x << SizeShift) | z;
                GenerateColumn(x, z, column.Heights[i], column.DirtDepth[i], lowerBoundY);
            }
        }

        var worldX0 = (int)Position.X * Size;
        var worldZ0 = (int)Position.Z * Size;

        if (WorldGenerationConfig.NoiseCavesEnabled)
            NoiseCaves.Carve(_blocks, column, worldX0, lowerBoundY, worldZ0);

        if (WorldGenerationConfig.WormCavesEnabled)
            WormCarver.Carve(_blocks, column, worldX0, lowerBoundY, worldZ0);

        // After the carvers (so caves under the sea floor flood) and before the trees and
        // pebbles, which skip submerged columns rather than growing underwater
        if (WorldGenerationConfig.WaterEnabled)
            FillWater(column, lowerBoundY);

        if (WorldGenerationConfig.TreesEnabled)
            TreeGenerator.Stamp(_blocks, worldX0, lowerBoundY, worldZ0);

        if (WorldGenerationConfig.PebblesEnabled)
            PebbleGenerator.Stamp(_blocks, column, worldX0, lowerBoundY, worldZ0);

        // Carving removed an unknown number of blocks; a recount is cheaper than bookkeeping
        var solid = 0;
        foreach (var block in _blocks)
            if (block != BlockType.Air) solid++;
        _solidCount = solid;

        // Only a chunk that actually got flooded can have a frontier to report
        if (needsWater) ScanFluidFrontier();

        IsBlockDataReady = true;
    }

    /// <summary>
    /// Collects the cells where this chunk's water already touches space it could flow into,
    /// into <see cref="FluidFrontier"/>. The sea is generated as static sources, so without
    /// this a cave or entrance that breached it would leave a wall of water standing beside
    /// worldgen air forever — nothing schedules those cells, since only block edits do.
    ///
    /// +Y is deliberately not tested: every sea-surface cell has air above it and water never
    /// flows upward, so including it would report a useless 256 cells per surface chunk.
    /// Neighbours across a chunk border are left to the main-thread pass, which can wait until
    /// both chunks are loaded, so this only ever reads this chunk's own blocks and stays as
    /// thread-safe as the rest of worldgen.
    /// </summary>
    private void ScanFluidFrontier()
    {
        List<ushort>? frontier = null;

        for (var x = 0; x < Size; x++)
        for (var y = 0; y < Size; y++)
        for (var z = 0; z < Size; z++)
        {
            var i = Idx(x, y, z);
            if (!BlockType.IsFluid(_blocks[i])) continue;

            var exposed = (x > 0 && FluidManager.IsFlowTarget(_blocks[Idx(x - 1, y, z)]))
                          || (x < SizeMask && FluidManager.IsFlowTarget(_blocks[Idx(x + 1, y, z)]))
                          || (z > 0 && FluidManager.IsFlowTarget(_blocks[Idx(x, y, z - 1)]))
                          || (z < SizeMask && FluidManager.IsFlowTarget(_blocks[Idx(x, y, z + 1)]))
                          || (y > 0 && FluidManager.IsFlowTarget(_blocks[Idx(x, y - 1, z)]));
            if (!exposed) continue;

            frontier ??= [];
            frontier.Add((ushort)i);
        }

        FluidFrontier = frontier?.ToArray();
    }

    /// <summary>
    /// Floods every air cell up to and including sea level in columns whose terrain sits below
    /// it, so oceans, flooded ravines and sea-floor caves all fill. Generated water is always a
    /// source (state 0), which is what an untouched state byte reads as, so nothing is written
    /// to <c>_state</c> here. A pure per-column function of world position like the rest of
    /// worldgen: neighbours agree at their borders with no shared state.
    /// </summary>
    private void FillWater(ColumnData column, int lowerBoundY)
    {
        var top = Math.Min(WorldGenerationConfig.SeaLevel - lowerBoundY, Size - 1);
        if (top < 0) return;

        for (var x = 0; x < Size; x++)
        for (var z = 0; z < Size; z++)
        {
            if (column.Heights[(x << SizeShift) | z] >= WorldGenerationConfig.SeaLevel) continue;

            for (var y = 0; y <= top; y++)
            {
                var i = Idx(x, y, z);
                if (_blocks[i] == BlockType.Air) _blocks[i] = TerrainGenerator.SeaBlock;
            }
        }
    }

    /// <summary>
    /// Returns the 2D generation inputs for a chunk column. Nothing generated is ever solid
    /// above Heights[x, z]; the block at that height is solid unless a cave or entrance carved
    /// it. Cached, deterministic, and safe to call from any thread.
    /// </summary>
    public static ColumnData GetColumnData(int chunkX, int chunkZ)
    {
        var key = (chunkX, chunkZ);
        if (ColumnCache.TryGetValue(key, out var cached))
            return cached;

        var column = TerrainGenerator.Compute(chunkX, chunkZ);

        // Crude growth cap; entries are deterministic so a full reset is harmless
        if (ColumnCache.Count >= ColumnCacheLimit)
            ColumnCache.Clear();

        ColumnCache[key] = column;
        return column;
    }

    /// <summary>
    /// The terrain heightmap for a chunk column, indexed by (x &lt;&lt; SizeShift) | z. Ravines
    /// are already cut in; caves are not (they are 3D). See <see cref="GetColumnData"/>.
    /// </summary>
    public static int[] GetColumnHeights(int chunkX, int chunkZ) => GetColumnData(chunkX, chunkZ).Heights;

    // Solid from the chunk floor up to the terrain height: grass on top, dirt for dirtDepth
    // blocks under it, stone below that. dirtDepth == 0 marks a bare stone column
    // (see ColumnData.DirtDepth) - deep ravine walls, where the top block is stone as well.
    // A submerged column tops out in dirt instead: grass does not grow on a sea floor.
    // Shoreline columns (see TerrainGenerator.IsBeachHeight) build from sand instead, capped
    // with gravel where TerrainGenerator.IsGravelPatch holds.
    private void GenerateColumn(int x, int z, int terrainHeight, int dirtDepth, int lowerBoundY)
    {
        var topBlock = TerrainGenerator.IsSubmerged(terrainHeight)
            ? TerrainGenerator.SoilBlock
            : TerrainGenerator.SurfaceBlock;
        var soilBlock = TerrainGenerator.SoilBlock;

        if (dirtDepth != 0 && TerrainGenerator.IsBeachHeight(terrainHeight))
        {
            var worldX = (int)Position.X * Size + x;
            var worldZ = (int)Position.Z * Size + z;
            topBlock = TerrainGenerator.IsGravelPatch(worldX, worldZ)
                ? TerrainGenerator.GravelBlock
                : TerrainGenerator.SandBlock;
            soilBlock = TerrainGenerator.SandBlock;
        }

        for (var y = 0; y < Size; y++)
        {
            var depth = terrainHeight - (lowerBoundY + y);
            if (depth < 0) break;

            _blocks[Idx(x, y, z)] = dirtDepth == 0 ? TerrainGenerator.RockBlock
                : depth == 0 ? topBlock
                : depth <= dirtDepth ? soilBlock
                : TerrainGenerator.RockBlock;
        }
    }

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

        return previous;
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

        chunkManager.UnmarkPlayerPlaced((int)worldPos.X, (int)worldPos.Y, (int)worldPos.Z);
        chunkManager.ImmediateRemesh(Position);
        MarkNeighborsDirty(worldPos, chunkManager);
        return true;
    }

    /// <summary>
    /// Fills a block. Returns false if the cell is outside this chunk or already occupied, so
    /// callers can avoid spending an item on a placement that never happened.
    /// </summary>
    public bool PlaceBlockAt(Vector3 worldPos, byte blockId, ChunkManager chunkManager)
    {
        var localX = (int)(worldPos.X - Position.X * Size);
        var localY = (int)(worldPos.Y - Position.Y * Size);
        var localZ = (int)(worldPos.Z - Position.Z * Size);

        if (!InBounds(localX, localY, localZ)) return false;
        // Water is displaced by a placement instead of blocking it; anything else occupies
        // the cell and the placement fails, so the caller keeps its item
        var existing = _blocks[Idx(localX, localY, localZ)];
        if (existing != 0 && !BlockType.IsFluid(existing)) return false;

        _blocks[Idx(localX, localY, localZ)] = blockId;
        if (existing == 0) _solidCount++;
        ClearState(Idx(localX, localY, localZ));

        chunkManager.MarkPlayerPlaced((int)worldPos.X, (int)worldPos.Y, (int)worldPos.Z);
        chunkManager.ImmediateRemesh(Position);
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
    /// Builds this chunk's mesh, or returns null when a face neighbour is missing.
    /// The whole 3x3x3 neighbourhood is resolved once, up front: the caller checked for face
    /// neighbours before scheduling, but this runs on a pool thread behind a semaphore and can
    /// start seconds later, by which time an unload may have taken one away. Meshing against
    /// that hole reads solid ground as air and bakes bogus side faces, sunk fluid corners and
    /// sunlight leaks along the border, which then stay until something re-dirties the chunk —
    /// so the build is abandoned instead and the caller puts the chunk back on the dirty list.
    /// </summary>
    public ChunkMeshData? GenerateMeshData(ChunkManager chunkManager)
    {
        // Resolve before renting or touching anything: an abandoned build should cost nothing
        var neighbors = new Chunk?[27];
        byte[]?[] neighborStates = new byte[27][];
        if (!TryResolveNeighbors(chunkManager, neighbors, neighborStates))
            return null;

        // Nothing to draw: either all air, or a solid block of occluders whose six neighbours
        // seal every boundary face. The second case is most of the deep terrain in the load
        // sphere, and skipping it saves the light flood and the per-voxel walk below.
        if (_solidCount == 0 || IsFullyEnclosed(neighbors))
            return new ChunkMeshData([], [], [], []);

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
            Array.Clear(sunLight, 0, LightCount);
            Array.Clear(blockLight, 0, LightCount);
            ComputeLight(lightBlocks, sunLight, blockLight, queue);
            return GenerateMeshDataFromSnapshot(padded, paddedState, sunLight, blockLight, chunkManager.MeshBuffers);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(padded);
            ArrayPool<byte>.Shared.Return(paddedState);
            ArrayPool<byte>.Shared.Return(lightBlocks);
            ArrayPool<byte>.Shared.Return(sunLight);
            ArrayPool<byte>.Shared.Return(blockLight);
            ArrayPool<int>.Shared.Return(queue);
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

            var pos = Position + new Vector3(cx - 1, cy - 1, cz - 1);
            // Each neighbour's state array is read once into the array: the field is volatile
            // and may be allocated by a main-thread fluid edit while the snapshot is filled
            if (chunkManager.Chunks.TryGetValue(pos, out var neighbor) && neighbor.IsBlockDataReady)
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
    /// Turns the padded block snapshot into vertices. Each block picks an emitter from its
    /// <see cref="BlockType.Renderer"/>: the two procedural ones (tapered wood, pebbles) build
    /// their geometry from tiles, everything else replays its baked model's quads.
    /// </summary>
    private ChunkMeshData GenerateMeshDataFromSnapshot(byte[] padded, byte[] paddedState,
        byte[] sunLight, byte[] blockLight, MeshBufferPool pool)
    {
        // Rented, not allocated: ChunkManager returns them once the mesh is on the GPU
        var vertices = pool.RentVertices();
        var indices = pool.RentIndices();
        ushort indexOffset = 0;

        // Fluids build a second, blended mesh drawn after the opaque world, so whatever sits
        // behind the water shows through it. Most chunks hold none, hence the tiny capacity.
        var tVertices = new List<Vertex>();
        var tIndices = new List<ushort>();
        ushort tIndexOffset = 0;

        for (var x = 0; x < Size; x++)
        for (var y = 0; y < Size; y++)
        for (var z = 0; z < Size; z++)
        {
            var blockId = _blocks[Idx(x, y, z)];
            if (blockId == 0) continue;

            var type = BlockType.ById[blockId];
            if (type == null) continue;

            var basePos = new Vector3(x, y, z);
            var pIdx = (x + 1) * PaddedSq + (y + 1) * Padded + (z + 1);

            // Tapered wood renders as a centred box (plus arms reaching wood neighbours)
            // instead of a full cube; see EmitBranchBox
            if (type.Renderer == BlockRenderer.TaperedWood)
            {
                EmitBranchBox(vertices, indices, ref indexOffset, padded, pIdx, sunLight, blockLight,
                    x, y, z, type, basePos);
                continue;
            }

            // Pebbles render as 1-3 ankle-high boxes sharing the cell; see EmitPebbles
            if (type.Renderer == BlockRenderer.Pebbles)
            {
                EmitPebbles(vertices, indices, ref indexOffset, padded, pIdx, sunLight, blockLight,
                    x, y, z, type, basePos);
                continue;
            }

            // A fluid's sloped surface is read out of paddedState and goes into the
            // translucent lists; see EmitFluid
            if (type.Renderer == BlockRenderer.Fluid)
            {
                EmitFluid(tVertices, tIndices, ref tIndexOffset, padded, paddedState, pIdx,
                    sunLight, blockLight, x, y, z, type, basePos);
                continue;
            }

            var model = type.BakedModel;
            if (model == null) continue;

            // Canopy leaves ride the wind in the vertex shader; everything else stays planted
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
                var cullFace = quad.CullFace;
                int lightCell;

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

                    // Light of the air cell in front of the face, from the wide light volume
                    // (so lamps across the chunk border still reach); AO still varies corners.
                    // A neighbour that would read as black light is exactly one that occludes,
                    // and that face is already gone, so no fallback is needed here.
                    lightCell = lightBase + LightFaceDelta[cullFace];
                }
                else
                {
                    // An interior quad of a sub-cell model has no neighbour to read: light it
                    // from its own cell, which passes light precisely because the model is not
                    // a full cube (the generalised form of the pebble trick).
                    lightCell = lightBase;
                }

                var sun = sunLight[lightCell] / 15f;
                var block = blockLight[lightCell] / 15f;
                var face = quad.Face;
                var positions = quad.Positions;
                var uvs = quad.Uvs;
                var normal = quad.Normal;

                if (!quad.Shade)
                {
                    // Minecraft's per-element "shade": false — full brightness, no face
                    // brightness and no ambient occlusion
                    for (var j = 0; j < 4; j++)
                        dstVertices.Add(new Vertex(basePos + positions[j], 255, 255, 255, 255, uvs[j],
                            sun, block, normal, sway));
                }
                else if (quad.IsFullFace)
                {
                    // Only a quad that fills its cube face has meaningful corner neighbours,
                    // so per-corner AO applies exactly as it did for hand-written cubes
                    var aoDeltas = AODeltas[face];
                    for (var j = 0; j < 4; j++)
                    {
                        var side1 = BlockType.CastsAO(padded[pIdx + aoDeltas[j * 3 + 0]]);
                        var side2 = BlockType.CastsAO(padded[pIdx + aoDeltas[j * 3 + 1]]);
                        var corner = BlockType.CastsAO(padded[pIdx + aoDeltas[j * 3 + 2]]);

                        var ao = VertexAO(side1, side2, corner);
                        var shade = ShadeLut[(face << 2) | ao];

                        dstVertices.Add(new Vertex(basePos + positions[j], shade, shade, shade, 255, uvs[j],
                            sun, block, normal, sway));
                    }
                }
                else
                {
                    // A sub-cell quad floats inside the cell, where corner sampling would
                    // darken it by neighbours it does not touch: shade it flat and unoccluded
                    var shade = ShadeLut[(face << 2) | 3];
                    for (var j = 0; j < 4; j++)
                        dstVertices.Add(new Vertex(basePos + positions[j], shade, shade, shade, 255, uvs[j],
                            sun, block, normal, sway));
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

        return new ChunkMeshData(vertices, indices, tVertices, tIndices);
    }

    /// <summary>
    /// Emits one fluid cell's surface into the translucent lists. The top face is a quad whose
    /// four corners sit at <see cref="CornerHeight"/>, so a flow slopes away from its source
    /// and - the average being symmetric between neighbours - adjacent tops meet exactly with
    /// no crack. No face is ever emitted between two fluid cells, so a body of water reads as
    /// a single skin instead of a stack of blended cubes.
    /// </summary>
    private void EmitFluid(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, byte[] paddedState, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos)
    {
        // Water passes light and casts no AO, so its own cell carries the light for every
        // face and there is no per-corner shading to compute
        var lightBase = LightIdx(x, y, z);
        var sun = sunLight[lightBase] / 15f;
        var block = blockLight[lightBase] / 15f;

        // Corner heights indexed by the corner's own (X, Z) inside the cell: cornerX * 2 + cornerZ
        Span<float> corners = stackalloc float[4];
        var minCorner = float.MaxValue;
        for (var cx = 0; cx < 2; cx++)
        for (var cz = 0; cz < 2; cz++)
        {
            var h = CornerHeight(padded, paddedState, pIdx,
                cx == 0 ? -PaddedSq : PaddedSq, cz == 0 ? -1 : 1);
            corners[cx * 2 + cz] = h;
            if (h < minCorner) minCorner = h;
        }

        // A fluid's model only supplies its tile (see the block schema), so every face uses it
        var tile = type.Top;

        for (var face = 0; face < 6; face++)
        {
            var neighbour = padded[pIdx + FaceNeighborDelta[face]];

            // Two fluid cells share no face: their sloped tops already meet at the corners
            if (BlockType.IsFluid(neighbour)) continue;
            if (face != 4 && BlockType.Occludes(neighbour)) continue;
            // A full column pressed against a ceiling has a coplanar top face - drop it
            // instead of z-fighting the block above
            if (face == 4 && BlockType.Occludes(neighbour) && minCorner >= 0.999f) continue;

            // Meshes are indexed with ushort (mirrors LodRegion.AddQuad): stop emitting
            // instead of wrapping the offset if a chunk somehow runs past the limit
            if (vertices.Count > ushort.MaxValue - 3) return;

            var faceCorners = CubeFaces[face].face;
            var a = LowerCorner(faceCorners[0], corners);
            var b = LowerCorner(faceCorners[1], corners);
            var c = LowerCorner(faceCorners[2], corners);
            var d = LowerCorner(faceCorners[3], corners);

            // A sloped top needs the normal of the quad it actually became, or the sun
            // diffuse ignores the slope; the other five faces stay axis-aligned
            var normal = face == 4
                ? Vector3.Normalize(Vector3.Cross(c - a, d - b))
                : CubeFaces[face].normal;

            // ponytail: alpha 254 flags water for the shader (glass stays 255); fColor.a is unused otherwise.
            EmitQuad(vertices, indices, ref indexOffset, basePos, a, b, c, d, normal,
                CropUv(tile, face, a), CropUv(tile, face, b), CropUv(tile, face, c), CropUv(tile, face, d),
                ShadeLut[(face << 2) | 3], sun, block, 254);
        }
    }

    /// <summary>Drops a cube corner's top vertex to the fluid surface, so side faces crop to
    /// the real height and the top face slopes. Bottom corners pass through unchanged.</summary>
    private static Vector3 LowerCorner(Vector3 corner, Span<float> cornerHeights) =>
        corner.Y == 0f
            ? corner
            : new Vector3(corner.X, cornerHeights[(int)corner.X * 2 + (int)corner.Z], corner.Z);

    /// <summary>Surface height inside one fluid cell of the padded snapshot, 0..1. Fluid
    /// directly above fills the cell completely, so stacked water shows no seam.</summary>
    private static float FluidHeight(byte[] padded, byte[] paddedState, int p) =>
        BlockType.IsFluid(padded[p + FaceNeighborDelta[4]])
            ? 1f
            : FluidState.SurfaceHeight(paddedState[p]);

    /// <summary>
    /// Height of the fluid surface at one corner of a cell, averaged over the cells that meet
    /// there (<paramref name="dx"/> and <paramref name="dz"/> are the padded index deltas
    /// toward the corner). Nearly full cells outweigh thin ones ten to one, so a source next
    /// to a skirt keeps its own level; air and cutouts pull the corner down toward 0; an
    /// occluding solid is skipped, because a wall must not lower the water beside it. The
    /// diagonal cell counts only when at least one of the two orthogonal neighbours is fluid
    /// (Minecraft's rule), so two bodies of water touching across a corner with solids in both
    /// orthogonal cells stay separate instead of visually joining. The cell itself is fluid, so
    /// the weight is never zero.
    /// </summary>
    private static float CornerHeight(byte[] padded, byte[] paddedState, int p, int dx, int dz)
    {
        var total = 0f;
        var weight = 0f;
        // The diagonal is only reachable through a fluid orthogonal neighbour; with neither of
        // them fluid it is ignored outright - no height, no full-column pin, no weight
        var orthogonalFluid = false;

        for (var i = 0; i < 4; i++)
        {
            // i: 0 = own cell, 1 = the dx neighbour, 2 = the dz neighbour, 3 = the diagonal
            if (i == 3 && !orthogonalFluid) break;

            var q = p + ((i & 1) != 0 ? dx : 0) + ((i & 2) != 0 ? dz : 0);
            var id = padded[q];

            if (BlockType.IsFluid(id))
            {
                if (i is 1 or 2) orthogonalFluid = true;

                // A full column touching this corner pins it to the cell top
                if (BlockType.IsFluid(padded[q + FaceNeighborDelta[4]])) return 1f;

                var h = FluidHeight(padded, paddedState, q);
                if (h >= 0.8f)
                {
                    total += h * 10f;
                    weight += 10f;
                }
                else
                {
                    total += h;
                    weight += 1f;
                }
            }
            else if (!BlockType.Occludes(id))
            {
                weight += 1f;
            }
        }

        return total / weight;
    }

    /// <summary>Half-width of wood for joint comparisons; logs count as full width.</summary>
    private static float WoodHalfWidth(byte blockId) => BlockType.WoodHalfWidth(blockId);

    private static bool IsWood(byte blockId) => WoodHalfWidth(blockId) >= 0f;

    /// <summary>
    /// Emits a tapered wood segment as a centred node cube with square tubes reaching every
    /// wood neighbour (plus a foot onto full cubes, so a segment sitting on terrain or a log
    /// never floats). A vertical stack of segments reads as one continuous tapering pipe, but
    /// a limb cell with only horizontal neighbours is just a node with arms — never a
    /// full-height vertical core, so limbs read as limbs instead of second trunks. Tube mouths
    /// press against the node faces behind them and cap only into narrower wood; wider wood
    /// and full cubes already close the joint. No face culling is enabled, so only normals
    /// matter — but coplanar opposite quads would still z-fight, and these rules emit none.
    /// </summary>
    private static void EmitBranchBox(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos)
    {
        var w = type.HalfWidth;
        var c = 0.5f;

        Span<byte> nb = stackalloc byte[6];
        Span<float> sunF = stackalloc float[6];
        Span<float> blockF = stackalloc float[6];
        var lightBase = LightIdx(x, y, z);
        for (var i = 0; i < 6; i++)
        {
            nb[i] = padded[pIdx + FaceNeighborDelta[i]];
            var lightCell = lightBase + LightFaceDelta[i];
            sunF[i] = sunLight[lightCell] / 15f;
            blockF[i] = blockLight[lightCell] / 15f;
        }

        // Central node: every face draws, closing the tube mouths pressed against it
        var nodeMin = new Vector3(c - w, c - w, c - w);
        var nodeMax = new Vector3(c + w, c + w, c + w);
        for (var i = 0; i < 6; i++)
        {
            var tile = i == 4 ? type.Top : i == 5 ? type.Bottom : type.Side;
            EmitBoxFace(vertices, indices, ref indexOffset, basePos, nodeMin, nodeMax, i,
                tile, ShadeLut[(i << 2) | 3], sunF[i], blockF[i]);
        }

        // Up-tube continues the taper into wood stacked above
        var aboveWidth = WoodHalfWidth(nb[4]);
        if (aboveWidth >= 0f)
        {
            EmitTubeSides(vertices, indices, ref indexOffset, basePos, sunF, blockF,
                type.Side, c - w, c + w, c + w, 1f);
            if (aboveWidth < w)
                EmitTubeCap(vertices, indices, ref indexOffset, padded, pIdx,
                    sunF[4], blockF[4], type.Top, basePos, c, w, top: true);
        }

        // Down-tube continues into wood below, or feet onto a full cube; air below means the
        // node floats free (a limb in open air) and its bottom face already closes it
        var belowWidth = WoodHalfWidth(nb[5]);
        if (nb[5] != BlockType.Air)
        {
            EmitTubeSides(vertices, indices, ref indexOffset, basePos, sunF, blockF,
                type.Side, c - w, c + w, 0f, c - w);
            if (belowWidth >= 0f && belowWidth < w)
                EmitTubeCap(vertices, indices, ref indexOffset, padded, pIdx,
                    sunF[5], blockF[5], type.Bottom, basePos, c, w, top: false);
        }

        // Horizontal arms weld this segment to wood neighbours
        for (var i = 0; i < 4; i++)
        {
            if (!IsWood(nb[i])) continue;
            EmitArm(vertices, indices, ref indexOffset, sunF, blockF, type, basePos, c, w, i, nb[i]);
        }
    }

    /// <summary>
    /// Emits the 1-3 ankle-high stones sharing a pebble cell: floor-sitting cubes sized and
    /// spun per stone (see PebbleGenerator), with the stone tile cropped to their size so
    /// texel density matches full cubes. UVs come from the unrotated corners, so the texture
    /// stays put while the geometry spins. The bottom face is skipped: it sits coplanar on
    /// the ground's top face, which still draws because pebbles never occlude.
    /// </summary>
    private void EmitPebbles(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, int pIdx, byte[] sunLight, byte[] blockLight,
        int x, int y, int z, BlockType type, Vector3 basePos)
    {
        var worldX = (int)Position.X * Size + x;
        var worldZ = (int)Position.Z * Size + z;
        var count = PebbleGenerator.CountAt(worldX, worldZ);
        var variant = PebbleGenerator.VariantAt(worldX, worldZ);

        Span<float> sunF = stackalloc float[6];
        Span<float> blockF = stackalloc float[6];
        var lightBase = LightIdx(x, y, z);
        for (var i = 0; i < 6; i++)
        {
            // A face backed against rock or wood (a slope, a trunk) must not sample the
            // neighbour's zero light, or it renders black: light it from the pebble's own
            // cell instead, which sits open to the sky above the ground it rests on.
            var lightCell = BlockType.Occludes(padded[pIdx + FaceNeighborDelta[i]])
                ? lightBase
                : lightBase + LightFaceDelta[i];
            sunF[i] = sunLight[lightCell] / 15f;
            blockF[i] = blockLight[lightCell] / 15f;
        }

        for (var b = 0; b < count; b++)
        {
            PebbleGenerator.GetCenter(count, variant, b, out var cx, out var cz);
            var s = PebbleGenerator.SizeAt(worldX, worldZ, b);
            var angle = PebbleGenerator.AngleAt(worldX, worldZ, b);
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            var half = s * 0.5f;
            var min = new Vector3(cx - half, 0f, cz - half);
            var max = new Vector3(cx + half, s, cz + half);
            for (var face = 0; face < 6; face++)
            {
                if (face == 5) continue;
                var tile = face == 4 ? type.Top : type.Side;
                BoxFaceCorners(min, max, face, out var p0, out var p1, out var p2, out var p3);
                var normal = CubeFaces[face].normal;
                EmitQuad(vertices, indices, ref indexOffset, basePos,
                    SpinY(p0, cx, cz, cos, sin), SpinY(p1, cx, cz, cos, sin),
                    SpinY(p2, cx, cz, cos, sin), SpinY(p3, cx, cz, cos, sin),
                    SpinY(normal, 0f, 0f, cos, sin),
                    CropUv(tile, face, p0), CropUv(tile, face, p1),
                    CropUv(tile, face, p2), CropUv(tile, face, p3),
                    ShadeLut[(face << 2) | 3], sunF[face], blockF[face]);
            }
        }
    }

    /// <summary>Rotates a point about (cx, cz) by a Y spin; normals pass through with cx = cz = 0.</summary>
    private static Vector3 SpinY(Vector3 p, float cx, float cz, float cos, float sin)
    {
        var dx = p.X - cx;
        var dz = p.Z - cz;
        return new Vector3(cx + dx * cos + dz * sin, p.Y, cz - dx * sin + dz * cos);
    }

    /// <summary>The four vertical walls of a tapered tube with a square [lo, hi] cross-section
    /// running from <paramref name="y0"/> to <paramref name="y1"/> (cell-local).</summary>
    private static void EmitTubeSides(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Vector3 basePos, Span<float> sunF, Span<float> blockF, BlockType.Point tile,
        float lo, float hi, float y0, float y1)
    {
        var min = new Vector3(lo, y0, lo);
        var max = new Vector3(hi, y1, hi);
        for (var i = 0; i < 4; i++)
            EmitBoxFace(vertices, indices, ref indexOffset, basePos, min, max, i,
                tile, ShadeLut[(i << 2) | 3], sunF[i], blockF[i]);
    }

    /// <summary>Mouth cap of a vertical tube where it meets air or narrower wood; wider wood
    /// and full cubes already cover the joint, so capping there would z-fight them.</summary>
    private static void EmitTubeCap(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        byte[] padded, int pIdx, float sun, float block, BlockType.Point tile,
        Vector3 basePos, float c, float w, bool top)
    {
        var i = top ? 4 : 5;
        var face = CubeFaces[i].face;
        var aoDeltas = AODeltas[i];

        for (var j = 0; j < 4; j++)
        {
            var corner = face[j];
            var side1 = BlockType.CastsAO(padded[pIdx + aoDeltas[j * 3 + 0]]);
            var side2 = BlockType.CastsAO(padded[pIdx + aoDeltas[j * 3 + 1]]);
            var occluder = BlockType.CastsAO(padded[pIdx + aoDeltas[j * 3 + 2]]);

            var ao = VertexAO(side1, side2, occluder);
            var shade = ShadeLut[(i << 2) | ao];

            var local = new Vector3(
                corner.X == 0 ? c - w : c + w,
                top ? 1f : 0f,
                corner.Z == 0 ? c - w : c + w);
            var pos = basePos + local;
            vertices.Add(new Vertex(pos, shade, shade, shade, 255,
                CropUv(tile, i, local), sun, block, CubeFaces[i].normal));
        }

        indices.Add((ushort)(indexOffset + 0));
        indices.Add((ushort)(indexOffset + 1));
        indices.Add((ushort)(indexOffset + 2));
        indices.Add((ushort)(indexOffset + 0));
        indices.Add((ushort)(indexOffset + 2));
        indices.Add((ushort)(indexOffset + 3));
        indexOffset += 4;
    }

    /// <summary>
    /// One horizontal limb from the node wall (so arm walls meet node walls edge-on with no
    /// coplanar overlap) to the cell face toward a wood neighbour.
    /// The end cap draws only into a narrower neighbour's mouth; equal or wider wood and full
    /// cubes already close the joint.
    /// </summary>
    private static void EmitArm(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Span<float> sunF, Span<float> blockF, BlockType type,
        Vector3 basePos, float c, float w, int dir, byte neighbourId)
    {
        var n = CubeFaces[dir].normal;
        var lo = c - w;
        var hi = c + w;
        // Box from the core wall out to the neighbour-side cell face
        var min = new Vector3(
            n.X > 0 ? hi : n.X < 0 ? 0f : lo,
            lo,
            n.Z > 0 ? hi : n.Z < 0 ? 0f : lo);
        var max = new Vector3(
            n.X > 0 ? 1f : n.X < 0 ? c - w : hi,
            hi,
            n.Z > 0 ? 1f : n.Z < 0 ? c - w : hi);

        // Top, bottom and the two flanks always draw; the mouth cap only into narrower wood
        EmitBoxFace(vertices, indices, ref indexOffset, basePos, min, max, 4,
            type.Side, ShadeLut[(4 << 2) | 3], sunF[4], blockF[4]);
        EmitBoxFace(vertices, indices, ref indexOffset, basePos, min, max, 5,
            type.Side, ShadeLut[(5 << 2) | 3], sunF[5], blockF[5]);

        var flankA = dir is 0 or 1 ? 2 : 0;
        var flankB = dir is 0 or 1 ? 3 : 1;
        EmitBoxFace(vertices, indices, ref indexOffset, basePos, min, max, flankA,
            type.Side, ShadeLut[(flankA << 2) | 3], sunF[flankA], blockF[flankA]);
        EmitBoxFace(vertices, indices, ref indexOffset, basePos, min, max, flankB,
            type.Side, ShadeLut[(flankB << 2) | 3], sunF[flankB], blockF[flankB]);

        if (IsWood(neighbourId) && WoodHalfWidth(neighbourId) < w)
            EmitBoxFace(vertices, indices, ref indexOffset, basePos, min, max, dir,
                type.Top, ShadeLut[(dir << 2) | 3], sunF[dir], blockF[dir]);
    }

    /// <summary>One axis-aligned face of the [min, max] box (cell-local), with flat shading
    /// and cropped UVs (see <see cref="CropUv"/>).</summary>
    private static void EmitBoxFace(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Vector3 basePos, Vector3 min, Vector3 max, int face,
        BlockType.Point tile, byte shade, float sun, float block)
    {
        BoxFaceCorners(min, max, face, out var a, out var b, out var c, out var d);
        EmitQuad(vertices, indices, ref indexOffset, basePos, a, b, c, d, CubeFaces[face].normal,
            CropUv(tile, face, a), CropUv(tile, face, b), CropUv(tile, face, c), CropUv(tile, face, d),
            shade, sun, block);
    }

    /// <summary>One arbitrary quad: four cell-local corners, one normal, one UV per corner.</summary>
    private static void EmitQuad(List<Vertex> vertices, List<ushort> indices, ref ushort indexOffset,
        Vector3 basePos, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal,
        Vector2 uva, Vector2 uvb, Vector2 uvc, Vector2 uvd,
        byte shade, float sun, float block, byte alpha = 255)
    {
        vertices.Add(new Vertex(basePos + a, shade, shade, shade, alpha, uva, sun, block, normal));
        vertices.Add(new Vertex(basePos + b, shade, shade, shade, alpha, uvb, sun, block, normal));
        vertices.Add(new Vertex(basePos + c, shade, shade, shade, alpha, uvc, sun, block, normal));
        vertices.Add(new Vertex(basePos + d, shade, shade, shade, alpha, uvd, sun, block, normal));

        indices.Add((ushort)(indexOffset + 0));
        indices.Add((ushort)(indexOffset + 1));
        indices.Add((ushort)(indexOffset + 2));
        indices.Add((ushort)(indexOffset + 0));
        indices.Add((ushort)(indexOffset + 2));
        indices.Add((ushort)(indexOffset + 3));
        indexOffset += 4;
    }

    /// <summary>Corners of one axis-aligned face of the [min, max] box (cell-local).</summary>
    private static void BoxFaceCorners(Vector3 min, Vector3 max, int face,
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
    /// Atlas UVs sampling the tile sub-region under a cell-local point, so a small box shows a
    /// crop of the texture at exactly the texel density of full cubes instead of a squashed
    /// whole tile. The mapping matches the full-cube face emission corner-for-corner (same
    /// mirroring per face), so bark features continue across log/branch joints at one scale.
    /// </summary>
    private static Vector2 CropUv(BlockType.Point tile, int face, Vector3 local)
    {
        float u = face switch
        {
            0 => local.X,
            1 => 1f - local.X,
            2 => 1f - local.Z,
            3 => local.Z,
            _ => local.X, // top and bottom run along X
        };
        float v = face switch
        {
            4 => 1f - local.Z, // top runs along Z
            5 => local.Z,      // bottom runs along Z
            _ => 1f - local.Y, // sides run bottom-to-top
        };
        return new Vector2((tile.X + u) * TextureAtlas.TileUVSize, (tile.Y + v) * TextureAtlas.TileUVSize);
    }

    public void UpdateMesh(ChunkMeshData meshData)
    {
        IsMeshed = true;
        _mesh?.Dispose();
        _mesh = meshData.Vertices.Count == 0
            ? null
            : new Mesh(_gl, CollectionsMarshal.AsSpan(meshData.Vertices), CollectionsMarshal.AsSpan(meshData.Indices));

        _translucentMesh?.Dispose();
        _translucentMesh = meshData.TranslucentVertices.Count == 0
            ? null
            : new Mesh(_gl, CollectionsMarshal.AsSpan(meshData.TranslucentVertices),
                CollectionsMarshal.AsSpan(meshData.TranslucentIndices));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int VertexAO(bool side1, bool side2, bool corner)
    {
        if (side1 && side2) return 0;
        return 3 - ((side1 ? 1 : 0) + (side2 ? 1 : 0) + (corner ? 1 : 0));
    }

    public void Render()
    {
        _mesh?.Render();
    }

    /// <summary>Draws only this chunk's fluid surfaces; called from the blended pass.</summary>
    public void RenderTranslucent()
    {
        _translucentMesh?.Render();
    }

    public void Dispose()
    {
        _mesh?.Dispose();
        _mesh = null;
        _translucentMesh?.Dispose();
        _translucentMesh = null;
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

    private static readonly Vector3[][][] AONeighbors =
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
/// opaque so the sea floor shows through the water.
/// </summary>
public record ChunkMeshData(List<Vertex> Vertices, List<ushort> Indices,
    List<Vertex> TranslucentVertices, List<ushort> TranslucentIndices);
